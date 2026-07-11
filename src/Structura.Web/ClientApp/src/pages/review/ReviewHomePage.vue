<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { api } from '../../api/client'
import EmptyState from '../../components/ui/EmptyState.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface ProjectItem {
  id: string
  name: string
  status: string
}

// Reviewers see the projects they belong to; record queues arrive with the review milestone.
const { data, isPending } = useQuery({
  queryKey: ['projects'],
  queryFn: () => api<{ items: ProjectItem[] }>('/api/projects'),
})
</script>

<template>
  <div class="flex flex-col gap-5">
    <div>
      <h1 class="text-lg font-semibold text-text">My Tasks</h1>
      <p class="text-sm text-muted">Records assigned to you for review will appear here.</p>
    </div>

    <SkeletonRows v-if="isPending" :rows="3" />

    <template v-else>
      <div v-if="(data?.items ?? []).length > 0" class="grid gap-3 sm:grid-cols-2">
        <div
          v-for="project in data!.items"
          :key="project.id"
          class="rounded-xl border border-border bg-surface p-4"
        >
          <h2 class="font-medium text-text" dir="auto">{{ project.name }}</h2>
          <p class="mt-2 text-sm text-muted">No records assigned to you yet.</p>
        </div>
      </div>

      <EmptyState
        v-else
        title="Nothing to review yet"
        description="You are not a member of any project yet. A project manager will add you and assign records to review."
      >
        <template #icon>🗂</template>
      </EmptyState>
    </template>
  </div>
</template>
