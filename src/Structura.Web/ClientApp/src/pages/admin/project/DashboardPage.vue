<script setup lang="ts">
import { computed, onUnmounted } from 'vue'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from '../../../api/client'
import { subscribeToProject, realtimeConnected } from '../../../api/signalr'
import { STATUS_BADGE } from '../../../api/types'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface StatusCount { status: string; count: number }
interface Dashboard {
  total: number
  processing: StatusCount[]
  review: StatusCount[]
  delivery: StatusCount[]
  tokens: { input: number; output: number; totalRuns: number }
  activeRun: { id: string; total: number; succeeded: number; failed: number } | null
  recentRuns: { id: string; status: string; total: number; succeeded: number; failed: number; createdAt: string }[]
}

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const queryClient = useQueryClient()

const { data, isPending } = useQuery({
  queryKey: ['project', projectId, 'dashboard'],
  queryFn: () => api<Dashboard>(`/api/projects/${projectId}/dashboard`),
  refetchInterval: (q) => (q.state.data?.activeRun && !realtimeConnected.value ? 3000 : false),
})

let cleanup: (() => void) | null = null
subscribeToProject(projectId, {
  RunProgress: () => queryClient.invalidateQueries({ queryKey: ['project', projectId, 'dashboard'] }),
  DeliveryProgress: () => queryClient.invalidateQueries({ queryKey: ['project', projectId, 'dashboard'] }),
}).then((fn) => (cleanup = fn))
onUnmounted(() => cleanup?.())

function count(list: StatusCount[] | undefined, status: string): number {
  return list?.find((s) => s.status === status)?.count ?? 0
}

const processingCards = computed(() => [
  { label: 'Pending', value: count(data.value?.processing, 'Pending'), variant: 'neutral' },
  { label: 'Processing', value: count(data.value?.processing, 'Processing'), variant: 'info' },
  { label: 'Completed', value: count(data.value?.processing, 'Completed'), variant: 'success' },
  { label: 'Failed', value: count(data.value?.processing, 'Failed'), variant: 'danger' },
])
const reviewCards = computed(() => [
  { label: 'Unassigned', value: count(data.value?.review, 'Unassigned'), variant: 'neutral' },
  { label: 'Assigned', value: count(data.value?.review, 'Assigned'), variant: 'info' },
  { label: 'In review', value: count(data.value?.review, 'InReview'), variant: 'primary' },
  { label: 'Approved', value: count(data.value?.review, 'Approved'), variant: 'success' },
  { label: 'Rejected', value: count(data.value?.review, 'Rejected'), variant: 'danger' },
  { label: 'Reprocess', value: count(data.value?.review, 'ReprocessRequested'), variant: 'warning' },
])
const deliveryCards = computed(() => [
  { label: 'Pending', value: count(data.value?.delivery, 'Pending'), variant: 'neutral' },
  { label: 'Delivered', value: count(data.value?.delivery, 'Delivered'), variant: 'success' },
  { label: 'Failed', value: count(data.value?.delivery, 'Failed'), variant: 'danger' },
])

function fmt(n: number): string {
  return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n)
}
</script>

<template>
  <SkeletonRows v-if="isPending" :rows="6" />

  <div v-else-if="data" class="flex flex-col gap-6">
    <!-- Active run banner -->
    <div
      v-if="data.activeRun"
      class="flex items-center gap-3 rounded-xl border border-primary/40 bg-primary-soft px-4 py-3"
    >
      <span class="relative flex h-2.5 w-2.5">
        <span class="absolute inline-flex h-full w-full animate-ping rounded-full bg-primary opacity-75" />
        <span class="relative inline-flex h-2.5 w-2.5 rounded-full bg-primary" />
      </span>
      <span class="text-sm font-medium text-primary">
        Processing run in progress — {{ data.activeRun.succeeded + data.activeRun.failed }} / {{ data.activeRun.total }}
      </span>
      <RouterLink :to="{ name: 'project-runs', params: { id: projectId } }" class="ml-auto text-sm text-primary underline-offset-2 hover:underline">
        View run →
      </RouterLink>
    </div>

    <section>
      <h2 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">
        Records ({{ data.total.toLocaleString() }} total)
      </h2>
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <div v-for="card in processingCards" :key="card.label" class="rounded-xl border border-border bg-surface p-4">
          <p class="text-2xl font-semibold text-text">{{ card.value.toLocaleString() }}</p>
          <p class="mt-1 text-xs text-muted">{{ card.label }}</p>
        </div>
      </div>
    </section>

    <section>
      <h2 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Review</h2>
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
        <div v-for="card in reviewCards" :key="card.label" class="rounded-xl border border-border bg-surface p-4">
          <p class="text-2xl font-semibold text-text">{{ card.value.toLocaleString() }}</p>
          <p class="mt-1 text-xs text-muted">{{ card.label }}</p>
        </div>
      </div>
    </section>

    <div class="grid gap-6 lg:grid-cols-2">
      <section>
        <h2 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Delivery (approved records)</h2>
        <div class="grid grid-cols-3 gap-3">
          <div v-for="card in deliveryCards" :key="card.label" class="rounded-xl border border-border bg-surface p-4">
            <p class="text-2xl font-semibold text-text">{{ card.value.toLocaleString() }}</p>
            <p class="mt-1 text-xs text-muted">{{ card.label }}</p>
          </div>
        </div>
      </section>

      <section>
        <h2 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">AI usage</h2>
        <div class="grid grid-cols-3 gap-3">
          <div class="rounded-xl border border-border bg-surface p-4">
            <p class="text-2xl font-semibold text-text">{{ fmt(data.tokens.input) }}</p>
            <p class="mt-1 text-xs text-muted">Input tokens</p>
          </div>
          <div class="rounded-xl border border-border bg-surface p-4">
            <p class="text-2xl font-semibold text-text">{{ fmt(data.tokens.output) }}</p>
            <p class="mt-1 text-xs text-muted">Output tokens</p>
          </div>
          <div class="rounded-xl border border-border bg-surface p-4">
            <p class="text-2xl font-semibold text-text">{{ data.tokens.totalRuns }}</p>
            <p class="mt-1 text-xs text-muted">Runs</p>
          </div>
        </div>
      </section>
    </div>

    <section v-if="data.recentRuns.length > 0">
      <h2 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Recent runs</h2>
      <ul class="divide-y divide-border overflow-hidden rounded-xl border border-border bg-surface">
        <li v-for="run in data.recentRuns" :key="run.id" class="flex items-center gap-3 px-4 py-2.5">
          <BaseBadge :variant="STATUS_BADGE[run.status] ?? 'neutral'">{{ run.status }}</BaseBadge>
          <span class="text-sm text-text">{{ run.total }} records</span>
          <span class="text-xs text-success">{{ run.succeeded }} ok</span>
          <span v-if="run.failed > 0" class="text-xs text-danger">{{ run.failed }} failed</span>
          <span class="ml-auto text-xs text-muted">{{ new Date(run.createdAt).toLocaleString() }}</span>
        </li>
      </ul>
    </section>
  </div>
</template>
