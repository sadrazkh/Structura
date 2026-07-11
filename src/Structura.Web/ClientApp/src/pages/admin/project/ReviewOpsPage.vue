<script setup lang="ts">
import { computed, ref } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { useToastStore } from '../../../stores/toast'
import BaseButton from '../../../components/ui/BaseButton.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface Member {
  userId: string
  fullName: string
  email: string
  role: string
  isActive: boolean
}

interface ReviewStatusReport {
  statusCounts: { status: string; count: number }[]
  perReviewer: {
    reviewerId: string
    fullName: string
    email: string
    pending: number
    approved: number
    rejected: number
    reprocessRequested: number
  }[]
}

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()

const statusQuery = useQuery({
  queryKey: ['project', projectId, 'review-status'],
  queryFn: () => api<ReviewStatusReport>(`/api/projects/${projectId}/review-status`),
  refetchInterval: 15_000,
})
const membersQuery = useQuery({
  queryKey: ['project', projectId, 'members'],
  queryFn: () => api<{ items: Member[] }>(`/api/projects/${projectId}/members`),
})

const counts = computed(() => {
  const map = new Map((statusQuery.data.value?.statusCounts ?? []).map((s) => [s.status, s.count]))
  return {
    unassigned: map.get('Unassigned') ?? 0,
    pending: (map.get('Assigned') ?? 0) + (map.get('InReview') ?? 0),
    approved: map.get('Approved') ?? 0,
    rejected: map.get('Rejected') ?? 0,
    reprocess: map.get('ReprocessRequested') ?? 0,
  }
})

// Unassigned counter counts every record incl. unprocessed; what matters for assignment
// is Completed+Unassigned — fetched separately.
const assignableQuery = useQuery({
  queryKey: ['project', projectId, 'assignable-count'],
  queryFn: async () => {
    const page = await api<{ items: { id: string }[]; nextCursor: string | null }>(
      `/api/projects/${projectId}/records?processingStatus=Completed&reviewStatus=Unassigned`)
    return { firstPage: page.items.map((i) => i.id), hasMore: page.nextCursor !== null }
  },
})

const selectedReviewers = ref(new Set<string>())
function toggleReviewer(id: string, checked: boolean) {
  const next = new Set(selectedReviewers.value)
  if (checked) next.add(id)
  else next.delete(id)
  selectedReviewers.value = next
}

/// Assigns every Completed+Unassigned record (page by page) to the selected reviewers.
const assignMutation = useMutation({
  mutationFn: async () => {
    const reviewerIds = [...selectedReviewers.value]
    let totalAssigned = 0
    for (let round = 0; round < 40; round++) {
      const page = await api<{ items: { id: string }[]; nextCursor: string | null }>(
        `/api/projects/${projectId}/records?processingStatus=Completed&reviewStatus=Unassigned`)
      if (page.items.length === 0) break
      const result = await api<{ assigned: number }>(`/api/projects/${projectId}/assignments`, {
        method: 'POST',
        body: reviewerIds.length === 1
          ? { recordIds: page.items.map((i) => i.id), mode: 'single', reviewerId: reviewerIds[0], reviewerIds: null }
          : { recordIds: page.items.map((i) => i.id), mode: 'distribute', reviewerId: null, reviewerIds },
      })
      totalAssigned += result.assigned
      if (page.nextCursor === null) break
    }
    return totalAssigned
  },
  onSuccess: (assigned) => {
    toast.success(`Assigned ${assigned} record(s).`)
    selectedReviewers.value = new Set()
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
  },
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Assignment failed.') : 'Assignment failed.'),
})

const unassignMutation = useMutation({
  mutationFn: async (reviewerId: string) => {
    let total = 0
    for (let round = 0; round < 40; round++) {
      const page = await api<{ items: { id: string }[]; nextCursor: string | null }>(
        `/api/projects/${projectId}/records?reviewerId=${reviewerId}&reviewStatus=Assigned`)
      if (page.items.length === 0) break
      const result = await api<{ unassigned: number }>(
        `/api/projects/${projectId}/assignments/unassign`,
        { method: 'POST', body: { recordIds: page.items.map((i) => i.id) } },
      )
      total += result.unassigned
      if (page.nextCursor === null || result.unassigned === 0) break
    }
    return total
  },
  onSuccess: (count) => {
    toast.success(`Returned ${count} untouched record(s) to the pool.`)
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
  },
  onError: () => toast.error('Unassign failed.'),
})
</script>

<template>
  <div class="flex flex-col gap-5">
    <!-- Status overview -->
    <div class="grid grid-cols-2 gap-3 sm:grid-cols-5">
      <div class="rounded-xl border border-border bg-surface p-3">
        <p class="text-xl font-semibold text-text">{{ assignableQuery.data.value?.hasMore ? '50+' : (assignableQuery.data.value?.firstPage.length ?? '…') }}</p>
        <p class="text-xs text-muted">Ready to assign</p>
      </div>
      <div class="rounded-xl border border-border bg-surface p-3">
        <p class="text-xl font-semibold text-text">{{ counts.pending }}</p>
        <p class="text-xs text-muted">In review</p>
      </div>
      <div class="rounded-xl border border-border bg-surface p-3">
        <p class="text-xl font-semibold text-success">{{ counts.approved }}</p>
        <p class="text-xs text-muted">Approved</p>
      </div>
      <div class="rounded-xl border border-border bg-surface p-3">
        <p class="text-xl font-semibold text-danger">{{ counts.rejected }}</p>
        <p class="text-xs text-muted">Rejected</p>
      </div>
      <div class="rounded-xl border border-border bg-surface p-3">
        <p class="text-xl font-semibold text-warning">{{ counts.reprocess }}</p>
        <p class="text-xs text-muted">Reprocess requested</p>
      </div>
    </div>

    <!-- Assign -->
    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">Assign records</h2>
      <p class="mb-4 text-sm text-muted">
        Distributes every processed, unassigned record evenly between the selected members.
      </p>
      <SkeletonRows v-if="membersQuery.isPending.value" :rows="2" />
      <template v-else>
        <div class="mb-4 flex flex-wrap gap-2">
          <label
            v-for="member in (membersQuery.data.value?.items ?? []).filter((m) => m.isActive)"
            :key="member.userId"
            class="flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm"
            :class="selectedReviewers.has(member.userId)
              ? 'border-primary bg-primary-soft text-primary'
              : 'border-border text-text hover:bg-bg'"
          >
            <input
              type="checkbox"
              class="h-4 w-4 accent-[var(--c-primary)]"
              :checked="selectedReviewers.has(member.userId)"
              @change="toggleReviewer(member.userId, ($event.target as HTMLInputElement).checked)"
            />
            {{ member.fullName }}
            <span class="text-xs text-muted">({{ member.role }})</span>
          </label>
          <p v-if="(membersQuery.data.value?.items ?? []).length === 0" class="text-sm text-muted">
            Add members in the Settings tab first.
          </p>
        </div>
        <BaseButton
          :disabled="isArchived || selectedReviewers.size === 0"
          :loading="assignMutation.isPending.value"
          @click="assignMutation.mutate()"
        >
          Assign All Ready Records
        </BaseButton>
      </template>
    </section>

    <!-- Per-reviewer table -->
    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-3 text-sm font-semibold text-text">Reviewers</h2>
      <SkeletonRows v-if="statusQuery.isPending.value" :rows="3" />
      <EmptyState
        v-else-if="(statusQuery.data.value?.perReviewer ?? []).length === 0"
        title="No assignments yet"
        description="Assign processed records to members above."
      >
        <template #icon>👥</template>
      </EmptyState>
      <div v-else class="overflow-x-auto">
        <table class="w-full min-w-[560px] text-left text-sm">
          <thead>
            <tr class="border-b border-border text-xs uppercase tracking-wide text-muted">
              <th class="px-3 py-2 font-medium">Reviewer</th>
              <th class="px-3 py-2 font-medium">Pending</th>
              <th class="px-3 py-2 font-medium">Approved</th>
              <th class="px-3 py-2 font-medium">Rejected</th>
              <th class="px-3 py-2 font-medium">Sent back</th>
              <th class="px-3 py-2"><span class="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody class="divide-y divide-border">
            <tr v-for="reviewer in statusQuery.data.value!.perReviewer" :key="reviewer.reviewerId">
              <td class="px-3 py-2.5">
                <p class="font-medium text-text">{{ reviewer.fullName }}</p>
                <p class="text-xs text-muted">{{ reviewer.email }}</p>
              </td>
              <td class="px-3 py-2.5 text-text">{{ reviewer.pending }}</td>
              <td class="px-3 py-2.5 text-success">{{ reviewer.approved }}</td>
              <td class="px-3 py-2.5 text-danger">{{ reviewer.rejected }}</td>
              <td class="px-3 py-2.5 text-warning">{{ reviewer.reprocessRequested }}</td>
              <td class="px-3 py-2.5 text-right">
                <BaseButton
                  v-if="reviewer.pending > 0 && !isArchived"
                  variant="ghost"
                  class="!px-2 !py-1 text-xs"
                  :loading="unassignMutation.isPending.value"
                  @click="unassignMutation.mutate(reviewer.reviewerId)"
                >
                  Unassign untouched
                </BaseButton>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="mt-3 text-xs text-muted">
        ⓘ Throughput depends on record difficulty — don't compare reviewers naively.
      </p>
    </section>
  </div>
</template>
