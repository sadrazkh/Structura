<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../api/client'
import { useAuthStore } from '../../stores/auth'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseInput from '../../components/ui/BaseInput.vue'
import BaseDialog from '../../components/ui/BaseDialog.vue'
import BaseBadge from '../../components/ui/BaseBadge.vue'
import EmptyState from '../../components/ui/EmptyState.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface ProjectItem {
  id: string
  name: string
  description: string
  status: 'Active' | 'Archived'
  schemaVersion: number
  memberCount: number
  createdAt: string
}

const auth = useAuthStore()
const toast = useToastStore()
const queryClient = useQueryClient()

const showArchived = ref(false)
const { data, isPending, isError, refetch } = useQuery({
  queryKey: ['projects'],
  queryFn: () => api<{ items: ProjectItem[] }>('/api/projects'),
})

const projects = computed(() =>
  (data.value?.items ?? []).filter((p) => showArchived.value || p.status === 'Active'),
)
const hasArchived = computed(() => (data.value?.items ?? []).some((p) => p.status === 'Archived'))

// --- Create dialog ---
const createOpen = ref(false)
const name = ref('')
const description = ref('')
const nameError = ref('')

const createMutation = useMutation({
  mutationFn: () =>
    api<{ id: string }>('/api/projects', {
      method: 'POST',
      body: { name: name.value.trim(), description: description.value.trim() },
    }),
  onSuccess: () => {
    toast.success('Project created.')
    createOpen.value = false
    name.value = ''
    description.value = ''
    queryClient.invalidateQueries({ queryKey: ['projects'] })
  },
  onError: (error) => {
    if (error instanceof ApiError && error.code === 'duplicate') {
      nameError.value = 'A project with this name already exists.'
    } else if (error instanceof ApiError && error.fieldError('name')) {
      nameError.value = error.fieldError('name')!
    } else {
      toast.error('Could not create the project.')
    }
  },
})

function openCreate() {
  nameError.value = ''
  createOpen.value = true
}

function formatDate(value: string) {
  return new Date(value).toLocaleDateString()
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h1 class="text-lg font-semibold text-text">Projects</h1>
        <p class="text-sm text-muted">Each project has its own schema, AI settings, and records.</p>
      </div>
      <BaseButton v-if="auth.canManageProjects" @click="openCreate">+ New Project</BaseButton>
    </div>

    <label v-if="hasArchived" class="flex items-center gap-2 text-sm text-muted">
      <input v-model="showArchived" type="checkbox" class="accent-[var(--c-primary)]" />
      Show archived projects
    </label>

    <SkeletonRows v-if="isPending" :rows="4" />

    <EmptyState
      v-else-if="isError"
      title="Could not load projects"
      description="Something went wrong while loading. Check your connection and try again."
    >
      <template #icon>⚠️</template>
      <template #action>
        <BaseButton variant="secondary" @click="refetch()">Try again</BaseButton>
      </template>
    </EmptyState>

    <EmptyState
      v-else-if="projects.length === 0"
      title="No projects yet"
      description="Create your first project to define a schema and start processing records."
    >
      <template #icon>📁</template>
      <template #action>
        <BaseButton v-if="auth.canManageProjects" @click="openCreate">+ New Project</BaseButton>
      </template>
    </EmptyState>

    <div v-else class="grid gap-3 sm:grid-cols-2">
      <RouterLink
        v-for="project in projects"
        :key="project.id"
        :to="{ name: 'project-settings', params: { id: project.id } }"
        class="group rounded-xl border border-border bg-surface p-4 transition-colors hover:border-primary/50"
      >
        <div class="flex items-start justify-between gap-2">
          <h2 class="font-medium text-text group-hover:text-primary">{{ project.name }}</h2>
          <BaseBadge :variant="project.status === 'Active' ? 'success' : 'neutral'">
            {{ project.status }}
          </BaseBadge>
        </div>
        <p class="mt-1 line-clamp-2 min-h-10 text-sm text-muted" dir="auto">
          {{ project.description || 'No description' }}
        </p>
        <div class="mt-3 flex items-center gap-4 text-xs text-muted">
          <span>👥 {{ project.memberCount }} member{{ project.memberCount === 1 ? '' : 's' }}</span>
          <span>🧩 Schema v{{ project.schemaVersion }}</span>
          <span>{{ formatDate(project.createdAt) }}</span>
        </div>
      </RouterLink>
    </div>

    <BaseDialog v-model="createOpen" title="New Project">
      <form class="flex flex-col gap-4" @submit.prevent="createMutation.mutate()">
        <BaseInput v-model="name" label="Name" :error="nameError" required placeholder="e.g. Incident Reports" />
        <BaseInput v-model="description" label="Description" placeholder="What is this project for?" />
      </form>
      <template #footer>
        <BaseButton variant="secondary" @click="createOpen = false">Cancel</BaseButton>
        <BaseButton :loading="createMutation.isPending.value" :disabled="!name.trim()" @click="createMutation.mutate()">
          Create Project
        </BaseButton>
      </template>
    </BaseDialog>
  </div>
</template>
