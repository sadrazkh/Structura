<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { api } from '../../../api/client'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import BaseButton from '../../../components/ui/BaseButton.vue'

export interface ProjectDetail {
  id: string
  name: string
  description: string
  status: 'Active' | 'Archived'
  schemaVersion: number
  memberCount: number
  createdAt: string
}

const route = useRoute()
const projectId = computed(() => route.params.id as string)

const { data: project, isPending, isError } = useQuery({
  queryKey: computed(() => ['project', projectId.value]),
  queryFn: () => api<ProjectDetail>(`/api/projects/${projectId.value}`),
})

const tabs = [
  { name: 'project-dashboard', label: 'Dashboard' },
  { name: 'project-records', label: 'Records' },
  { name: 'project-runs', label: 'Runs' },
  { name: 'project-review', label: 'Review' },
  { name: 'project-output', label: 'Output' },
  { name: 'project-schema', label: 'Schema' },
  { name: 'project-ai', label: 'AI Settings' },
  { name: 'project-import', label: 'Import' },
  { name: 'project-settings', label: 'Settings' },
]
</script>

<template>
  <SkeletonRows v-if="isPending" :rows="6" />

  <EmptyState
    v-else-if="isError || !project"
    title="Could not load this project"
    description="It may have been removed, or you may not have access to it."
  >
    <template #icon>⚠️</template>
    <template #action>
      <BaseButton variant="secondary" @click="$router.push({ name: 'projects' })">Back to Projects</BaseButton>
    </template>
  </EmptyState>

  <div v-else class="flex flex-col gap-5">
    <div class="flex flex-wrap items-center gap-3">
      <RouterLink :to="{ name: 'projects' }" class="text-sm text-muted hover:text-text">← Projects</RouterLink>
      <h1 class="text-lg font-semibold text-text" dir="auto">{{ project.name }}</h1>
      <BaseBadge :variant="project.status === 'Active' ? 'success' : 'neutral'">{{ project.status }}</BaseBadge>
      <BaseBadge variant="neutral">Schema v{{ project.schemaVersion }}</BaseBadge>
    </div>

    <nav class="flex gap-1 overflow-x-auto border-b border-border">
      <RouterLink
        v-for="tab in tabs"
        :key="tab.name"
        :to="{ name: tab.name, params: { id: projectId } }"
        class="-mb-px whitespace-nowrap border-b-2 border-transparent px-3 py-2 text-sm text-muted hover:text-text"
        active-class="!border-primary !text-primary font-medium"
      >
        {{ tab.label }}
      </RouterLink>
    </nav>

    <RouterView :project="project" />
  </div>
</template>
