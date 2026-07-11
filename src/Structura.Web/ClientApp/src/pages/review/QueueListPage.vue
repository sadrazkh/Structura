<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from '../../api/client'
import { useToastStore } from '../../stores/toast'
import { STATUS_BADGE } from '../../api/types'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseSelect from '../../components/ui/BaseSelect.vue'
import BaseBadge from '../../components/ui/BaseBadge.vue'
import EmptyState from '../../components/ui/EmptyState.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface QueueItem {
  id: string
  externalId: string
  textExcerpt: string
  reviewStatus: string
  hasDraft: boolean
  assignedAt: string | null
}

const route = useRoute()
const router = useRouter()
const toast = useToastStore()
const queryClient = useQueryClient()
const projectId = route.params.projectId as string

const statusFilter = ref('')
const queueQuery = useQuery({
  queryKey: computed(() => ['review', projectId, 'queue', statusFilter.value]),
  queryFn: () => api<{ items: QueueItem[] }>(
    `/api/review/${projectId}/records${statusFilter.value ? `?status=${statusFilter.value}` : ''}`),
})

const selection = ref(new Set<string>())
function toggle(id: string, checked: boolean) {
  const next = new Set(selection.value)
  if (checked) next.add(id)
  else next.delete(id)
  selection.value = next
}

const bulkApprove = useMutation({
  mutationFn: () =>
    api<{ approved: number; skipped: number; results: { recordId: string; ok: boolean; reason: string | null }[] }>(
      `/api/review/${projectId}/bulk-approve`,
      { method: 'POST', body: { recordIds: [...selection.value] } },
    ),
  onSuccess: (result) => {
    if (result.skipped === 0) {
      toast.success(`Approved ${result.approved} record(s).`)
    } else {
      const firstReason = result.results.find((r) => !r.ok)?.reason ?? ''
      toast.show('warning', `Approved ${result.approved}, skipped ${result.skipped}: ${firstReason}`)
    }
    selection.value = new Set()
    queryClient.invalidateQueries({ queryKey: ['review'] })
  },
  onError: () => toast.error('Bulk approve failed.'),
})

const statusOptions = [
  { value: '', label: 'To review' },
  { value: 'Approved', label: 'Approved' },
  { value: 'Rejected', label: 'Rejected' },
  { value: 'ReprocessRequested', label: 'Sent back for reprocessing' },
]

const canBulk = computed(() => statusFilter.value === '' && selection.value.size > 0)
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div class="flex items-center gap-3">
        <RouterLink :to="{ name: 'review-home' }" class="text-sm text-muted hover:text-text">← My Tasks</RouterLink>
        <h1 class="text-lg font-semibold text-text">Review Queue</h1>
      </div>
      <div class="w-56"><BaseSelect v-model="statusFilter" :options="statusOptions" /></div>
    </div>

    <div
      v-if="canBulk"
      class="flex items-center gap-3 rounded-lg border border-primary/40 bg-primary-soft px-3 py-2"
    >
      <span class="text-sm font-medium text-primary">{{ selection.size }} selected</span>
      <BaseButton class="!py-1" :loading="bulkApprove.isPending.value" @click="bulkApprove.mutate()">
        ✓ Bulk Approve
      </BaseButton>
      <BaseButton variant="ghost" class="!py-1" @click="selection = new Set()">Clear</BaseButton>
    </div>

    <SkeletonRows v-if="queueQuery.isPending.value" :rows="6" />

    <EmptyState
      v-else-if="(queueQuery.data.value?.items ?? []).length === 0"
      title="Nothing here"
      description="No records match this view."
    >
      <template #icon>🗂</template>
    </EmptyState>

    <ul v-else class="divide-y divide-border overflow-hidden rounded-xl border border-border bg-surface">
      <li
        v-for="item in queueQuery.data.value!.items"
        :key="item.id"
        class="flex cursor-pointer items-center gap-3 px-3 py-3 hover:bg-bg"
        @click="router.push({ name: 'review-focus', params: { projectId, recordId: item.id } })"
      >
        <input
          v-if="statusFilter === ''"
          type="checkbox"
          class="h-4 w-4 shrink-0 accent-[var(--c-primary)]"
          :checked="selection.has(item.id)"
          @click.stop
          @change="toggle(item.id, ($event.target as HTMLInputElement).checked)"
        />
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <span class="font-mono text-xs text-muted">{{ item.externalId }}</span>
            <BaseBadge :variant="STATUS_BADGE[item.reviewStatus] ?? 'neutral'">{{ item.reviewStatus }}</BaseBadge>
            <BaseBadge v-if="item.hasDraft && statusFilter === ''" variant="info">edited</BaseBadge>
          </div>
          <p class="mt-0.5 truncate text-sm text-text" dir="auto">{{ item.textExcerpt }}</p>
        </div>
        <span class="shrink-0 text-muted">›</span>
      </li>
    </ul>
  </div>
</template>
