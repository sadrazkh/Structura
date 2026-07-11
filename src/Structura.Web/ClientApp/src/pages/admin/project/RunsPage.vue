<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { subscribeToProject, realtimeConnected } from '../../../api/signalr'
import { useToastStore } from '../../../stores/toast'
import { STATUS_BADGE, type RunSummary } from '../../../api/types'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import ConfirmDialog from '../../../components/ui/ConfirmDialog.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface RunDetail {
  run: RunSummary
  failedRecords: { id: string; externalId: string; error: string | null }[]
}

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()

// ---------- Runs list with live updates ----------
const hasActiveRun = ref(false)
const runsQuery = useQuery({
  queryKey: ['project', projectId, 'runs'],
  queryFn: () => api<{ items: RunSummary[] }>(`/api/projects/${projectId}/runs`),
  // Read fresh data straight from the query (a watcher-updated ref would race the
  // interval evaluation, permanently disabling the polling fallback).
  refetchInterval: (query) => {
    const active = (query.state.data?.items ?? []).some((r) => r.status === 'Running')
    return active && !realtimeConnected.value ? 3000 : false
  },
})
watch(
  () => runsQuery.data.value,
  (data) => (hasActiveRun.value = (data?.items ?? []).some((r) => r.status === 'Running')),
  { immediate: true },
)

let cleanup: (() => void) | null = null
subscribeToProject(projectId, {
  RunProgress: () => {
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'runs'] })
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'record-counts'] })
    if (openRunId.value) queryClient.invalidateQueries({ queryKey: ['project', projectId, 'run', openRunId.value] })
  },
}).then((fn) => (cleanup = fn))
onUnmounted(() => cleanup?.())

// ---------- Pending count for the "process all" button ----------
const countsQuery = useQuery({
  queryKey: ['project', projectId, 'record-counts'],
  queryFn: () => api<{ processing: { status: string; count: number }[] }>(
    `/api/projects/${projectId}/records/counts`),
})
const pendingCount = computed(() =>
  countsQuery.data.value?.processing.find((g) => g.status === 'Pending')?.count ?? 0)

// ---------- Start run (all pending) ----------
const confirmProcessAll = ref(false)
const startMutation = useMutation({
  mutationFn: () =>
    api<RunSummary>(`/api/projects/${projectId}/runs`, {
      method: 'POST',
      body: { scope: 'allPending', recordIds: null },
    }),
  onSuccess: (run) => {
    confirmProcessAll.value = false
    toast.success(`Processing started — ${run.total} record(s).`)
    openRunId.value = run.id
    invalidateAll()
  },
  onError: (error) => {
    confirmProcessAll.value = false
    toast.error(error instanceof ApiError ? (error.detail ?? 'Could not start processing.') : 'Could not start processing.')
  },
})

// ---------- Run detail ----------
const openRunId = ref<string | null>(null)
const detailQuery = useQuery({
  queryKey: computed(() => ['project', projectId, 'run', openRunId.value]),
  queryFn: () => api<RunDetail>(`/api/projects/${projectId}/runs/${openRunId.value}`),
  enabled: computed(() => openRunId.value !== null),
  refetchInterval: () => (hasActiveRun.value && !realtimeConnected.value ? 3000 : false),
})

const cancelMutation = useMutation({
  mutationFn: (runId: string) => api(`/api/projects/${projectId}/runs/${runId}/cancel`, { method: 'POST' }),
  onSuccess: () => {
    toast.info('Cancel requested — records already sent to the model will finish.')
    invalidateAll()
  },
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Could not cancel.') : 'Could not cancel.'),
})

const retryMutation = useMutation({
  mutationFn: (runId: string) =>
    api<RunSummary>(`/api/projects/${projectId}/runs/${runId}/retry-failed`, { method: 'POST' }),
  onSuccess: (run) => {
    toast.success(`Retrying ${run.total} failed record(s).`)
    openRunId.value = run.id
    invalidateAll()
  },
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Could not retry.') : 'Could not retry.'),
})

function invalidateAll() {
  queryClient.invalidateQueries({ queryKey: ['project', projectId, 'runs'] })
  queryClient.invalidateQueries({ queryKey: ['project', projectId, 'record-counts'] })
  queryClient.invalidateQueries({ queryKey: ['project', projectId, 'records'] })
}

function progressPercent(run: RunSummary): number {
  if (run.total === 0) return 0
  return Math.min(100, Math.round(((run.succeeded + run.failed) / run.total) * 100))
}

function formatTokens(run: RunSummary): string {
  const fmt = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n))
  return `${fmt(run.inputTokens)} in / ${fmt(run.outputTokens)} out`
}

function duration(run: RunSummary): string {
  if (!run.startedAt) return '—'
  const end = run.finishedAt ? new Date(run.finishedAt) : new Date()
  const seconds = Math.max(1, Math.round((end.getTime() - new Date(run.startedAt).getTime()) / 1000))
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm text-muted">
        Send records to the AI model and track progress.
        <span v-if="pendingCount > 0" class="font-medium text-text">{{ pendingCount.toLocaleString() }} pending.</span>
      </p>
      <BaseButton
        :disabled="isArchived || pendingCount === 0"
        :loading="startMutation.isPending.value"
        @click="confirmProcessAll = true"
      >
        ▶ Process All Pending
      </BaseButton>
    </div>

    <SkeletonRows v-if="runsQuery.isPending.value" :rows="4" />

    <EmptyState
      v-else-if="(runsQuery.data.value?.items ?? []).length === 0"
      title="No processing runs yet"
      description="Import records, then process them here — or select records in the Records tab."
    >
      <template #icon>⚡</template>
    </EmptyState>

    <ul v-else class="flex flex-col gap-3">
      <li
        v-for="run in runsQuery.data.value!.items"
        :key="run.id"
        class="cursor-pointer rounded-xl border bg-surface p-4 transition-colors"
        :class="openRunId === run.id ? 'border-primary/60' : 'border-border hover:border-primary/40'"
        @click="openRunId = openRunId === run.id ? null : run.id"
      >
        <div class="flex flex-wrap items-center gap-2">
          <BaseBadge :variant="STATUS_BADGE[run.status] ?? 'neutral'">
            {{ run.cancelRequested && run.status === 'Running' ? 'Cancelling…' : run.status }}
          </BaseBadge>
          <span class="text-sm font-medium text-text">{{ run.total.toLocaleString() }} records</span>
          <span class="text-xs text-muted">· {{ run.model }}</span>
          <span class="text-xs text-muted">· {{ duration(run) }}</span>
          <span class="ml-auto flex items-center gap-3 text-xs">
            <span class="text-success">{{ run.succeeded }} ok</span>
            <span v-if="run.failed > 0" class="text-danger">{{ run.failed }} failed</span>
            <span class="text-muted">{{ formatTokens(run) }} tokens</span>
          </span>
        </div>

        <div class="mt-2.5 h-1.5 overflow-hidden rounded-full bg-bg">
          <div
            class="h-full rounded-full transition-all"
            :class="run.status === 'Running' ? 'bg-primary' : run.failed > 0 ? 'bg-warning' : 'bg-success'"
            :style="{ width: `${progressPercent(run)}%` }"
          />
        </div>

        <!-- Expanded detail -->
        <div v-if="openRunId === run.id" class="mt-3 border-t border-border pt-3" @click.stop>
          <div class="mb-3 flex flex-wrap gap-2">
            <BaseButton
              v-if="run.status === 'Running' && !run.cancelRequested"
              variant="secondary"
              class="!py-1 text-danger"
              :loading="cancelMutation.isPending.value"
              @click="cancelMutation.mutate(run.id)"
            >
              Cancel Run
            </BaseButton>
            <BaseButton
              v-if="run.status !== 'Running' && run.failed > 0 && !isArchived"
              variant="secondary"
              class="!py-1"
              :loading="retryMutation.isPending.value"
              @click="retryMutation.mutate(run.id)"
            >
              ↻ Retry {{ run.failed }} Failed
            </BaseButton>
            <span class="ml-auto self-center text-xs text-muted">
              Started {{ run.startedAt ? new Date(run.startedAt).toLocaleString() : '—' }}
            </span>
          </div>

          <template v-if="detailQuery.data.value && detailQuery.data.value.run.id === run.id">
            <div v-if="detailQuery.data.value.failedRecords.length > 0">
              <h3 class="mb-1.5 text-xs font-semibold uppercase tracking-wide text-danger">
                Failed records (first {{ detailQuery.data.value.failedRecords.length }})
              </h3>
              <ul class="divide-y divide-border rounded-lg border border-border">
                <li
                  v-for="failed in detailQuery.data.value.failedRecords"
                  :key="failed.id"
                  class="px-3 py-2 text-xs"
                >
                  <span class="font-mono text-text">{{ failed.externalId }}</span>
                  <span class="ml-2 text-danger">{{ failed.error }}</span>
                </li>
              </ul>
            </div>
            <p v-else-if="run.status !== 'Running'" class="text-xs text-muted">No failed records in this run.</p>
          </template>
        </div>
      </li>
    </ul>

    <ConfirmDialog
      v-model="confirmProcessAll"
      title="Process all pending records?"
      :message="`${pendingCount.toLocaleString()} record(s) will be sent to ${props.project.name}'s AI model. This calls the provider API and may incur costs.`"
      confirm-label="Start Processing"
      :loading="startMutation.isPending.value"
      @confirm="startMutation.mutate()"
    />
  </div>
</template>
