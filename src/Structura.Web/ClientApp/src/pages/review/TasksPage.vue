<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { api } from '../../api/client'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import EmptyState from '../../components/ui/EmptyState.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface TaskItem {
  projectId: string
  projectName: string
  pending: number
  inReview: number
}

const router = useRouter()
const toast = useToastStore()

const { data, isPending, isError, refetch } = useQuery({
  queryKey: ['review', 'tasks'],
  queryFn: () => api<{ items: TaskItem[] }>('/api/review/tasks'),
  refetchInterval: 30_000,
})

async function startReviewing(projectId: string) {
  const next = await api<{ recordId: string | null }>(`/api/review/${projectId}/next`)
  if (next.recordId) {
    router.push({ name: 'review-focus', params: { projectId, recordId: next.recordId } })
  } else {
    toast.info('Nothing left to review in this project.')
    refetch()
  }
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <div>
      <h1 class="text-lg font-semibold text-text">My Tasks</h1>
      <p class="text-sm text-muted">Records assigned to you for review.</p>
    </div>

    <SkeletonRows v-if="isPending" :rows="3" />

    <EmptyState
      v-else-if="isError"
      title="Could not load your tasks"
      description="Check your connection and try again."
    >
      <template #icon>⚠️</template>
      <template #action><BaseButton variant="secondary" @click="refetch()">Try again</BaseButton></template>
    </EmptyState>

    <EmptyState
      v-else-if="(data?.items ?? []).length === 0"
      title="All caught up 🎉"
      description="You have no records to review right now. New assignments will appear here."
    >
      <template #icon>🗂</template>
    </EmptyState>

    <div v-else class="grid gap-3 sm:grid-cols-2">
      <div
        v-for="task in data!.items"
        :key="task.projectId"
        class="flex flex-col gap-3 rounded-xl border border-border bg-surface p-4"
      >
        <div>
          <h2 class="font-medium text-text" dir="auto">{{ task.projectName }}</h2>
          <p class="mt-1 text-sm text-muted">
            <span class="text-xl font-semibold text-text">{{ task.pending }}</span>
            record{{ task.pending === 1 ? '' : 's' }} waiting
            <span v-if="task.inReview > 0"> · {{ task.inReview }} in progress</span>
          </p>
        </div>
        <div class="mt-auto flex gap-2">
          <BaseButton class="flex-1" @click="startReviewing(task.projectId)">Start Reviewing</BaseButton>
          <BaseButton
            variant="secondary"
            @click="router.push({ name: 'review-list', params: { projectId: task.projectId } })"
          >
            List
          </BaseButton>
        </div>
      </div>
    </div>
  </div>
</template>
