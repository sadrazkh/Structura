<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { useToastStore } from '../../../stores/toast'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import ConfirmDialog from '../../../components/ui/ConfirmDialog.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface Member {
  userId: string
  fullName: string
  email: string
  role: string
  isActive: boolean
  addedAt: string
}

const props = defineProps<{ project: ProjectDetail }>()

const router = useRouter()
const toast = useToastStore()
const queryClient = useQueryClient()
const projectId = props.project.id
const isArchived = () => props.project.status === 'Archived'

const membersQuery = useQuery({
  queryKey: ['project', projectId, 'members'],
  queryFn: () => api<{ items: Member[] }>(`/api/projects/${projectId}/members`),
})

// --- Details form ---
const name = ref(props.project.name)
const description = ref(props.project.description)
const nameError = ref('')
watch(
  () => props.project,
  (p) => {
    name.value = p.name
    description.value = p.description
  },
)

const saveMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}`, {
      method: 'PUT',
      body: { name: name.value.trim(), description: description.value.trim() },
    }),
  onSuccess: () => {
    toast.success('Project updated.')
    nameError.value = ''
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
    queryClient.invalidateQueries({ queryKey: ['projects'] })
  },
  onError: (error) => {
    if (error instanceof ApiError && error.code === 'duplicate') {
      nameError.value = 'A project with this name already exists.'
    } else {
      toast.error('Could not save the project.')
    }
  },
})

// --- Archive ---
const archiveOpen = ref(false)
const archiveMutation = useMutation({
  mutationFn: () => api(`/api/projects/${projectId}/archive`, { method: 'POST' }),
  onSuccess: () => {
    toast.success('Project archived.')
    archiveOpen.value = false
    queryClient.invalidateQueries({ queryKey: ['projects'] })
    router.push({ name: 'projects' })
  },
  onError: () => toast.error('Could not archive the project.'),
})

// --- Members ---
const memberEmail = ref('')
const memberError = ref('')
const addMemberMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}/members`, {
      method: 'POST',
      body: { email: memberEmail.value.trim() },
    }),
  onSuccess: () => {
    toast.success('Member added.')
    memberEmail.value = ''
    memberError.value = ''
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
  },
  onError: (error) => {
    if (error instanceof ApiError && error.code === 'not_found') {
      memberError.value = 'No user exists with this email.'
    } else if (error instanceof ApiError && error.code === 'duplicate') {
      memberError.value = 'This user is already a member.'
    } else if (error instanceof ApiError && error.fieldError('email')) {
      memberError.value = error.fieldError('email')!
    } else {
      memberError.value = 'Could not add the member.'
    }
  },
})

const removeTarget = ref<Member | null>(null)
const removeMemberMutation = useMutation({
  mutationFn: (userId: string) =>
    api(`/api/projects/${projectId}/members/${userId}`, { method: 'DELETE' }),
  onSuccess: () => {
    toast.success('Member removed.')
    removeTarget.value = null
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
  },
  onError: () => toast.error('Could not remove the member.'),
})

const roleBadge: Record<string, 'primary' | 'info' | 'neutral'> = {
  Administrator: 'primary',
  ProjectManager: 'info',
  Reviewer: 'neutral',
}
</script>

<template>
  <div class="flex flex-col gap-6">
    <!-- Details -->
    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-4 text-sm font-semibold text-text">Project Details</h2>
      <form class="flex max-w-lg flex-col gap-4" @submit.prevent="saveMutation.mutate()">
        <BaseInput v-model="name" label="Name" :error="nameError" :disabled="isArchived()" required />
        <BaseInput v-model="description" label="Description" :disabled="isArchived()" />
        <div>
          <BaseButton
            type="submit"
            :loading="saveMutation.isPending.value"
            :disabled="isArchived() || !name.trim()"
          >
            Save Changes
          </BaseButton>
        </div>
      </form>
    </section>

    <!-- Members -->
    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">Members</h2>
      <p class="mb-4 text-sm text-muted">
        Project managers manage this project; reviewers can review records assigned to them.
      </p>

      <form
        v-if="!isArchived()"
        class="mb-4 flex max-w-lg items-start gap-2"
        @submit.prevent="addMemberMutation.mutate()"
      >
        <div class="flex-1">
          <BaseInput v-model="memberEmail" placeholder="user@example.com" type="email" :error="memberError" />
        </div>
        <BaseButton
          type="submit"
          variant="secondary"
          :loading="addMemberMutation.isPending.value"
          :disabled="!memberEmail.trim()"
        >
          Add Member
        </BaseButton>
      </form>

      <SkeletonRows v-if="membersQuery.isPending.value" :rows="3" />
      <EmptyState
        v-else-if="(membersQuery.data.value?.items ?? []).length === 0"
        title="No members yet"
        description="Add project managers and reviewers by their email address."
      >
        <template #icon>👥</template>
      </EmptyState>
      <ul v-else class="divide-y divide-border">
        <li
          v-for="member in membersQuery.data.value!.items"
          :key="member.userId"
          class="flex items-center justify-between gap-3 py-2.5"
        >
          <div class="min-w-0">
            <p class="truncate text-sm font-medium text-text">
              {{ member.fullName }}
              <span v-if="!member.isActive" class="text-xs text-danger">(deactivated)</span>
            </p>
            <p class="truncate text-xs text-muted">{{ member.email }}</p>
          </div>
          <div class="flex shrink-0 items-center gap-2">
            <BaseBadge :variant="roleBadge[member.role] ?? 'neutral'">{{ member.role }}</BaseBadge>
            <BaseButton
              v-if="!isArchived()"
              variant="ghost"
              class="!px-2 !py-1 text-danger"
              @click="removeTarget = member"
            >
              Remove
            </BaseButton>
          </div>
        </li>
      </ul>
    </section>

    <!-- Danger zone -->
    <section v-if="!isArchived()" class="rounded-xl border border-danger/30 bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-danger">Danger Zone</h2>
      <p class="mb-3 text-sm text-muted">
        Archiving makes the project read-only. Records and settings are preserved.
      </p>
      <BaseButton variant="danger" @click="archiveOpen = true">Archive Project</BaseButton>
    </section>

    <ConfirmDialog
      v-model="archiveOpen"
      title="Archive this project?"
      :message="`&quot;${props.project.name}&quot; will become read-only for everyone.`"
      confirm-label="Archive"
      danger
      :loading="archiveMutation.isPending.value"
      @confirm="archiveMutation.mutate()"
    />

    <ConfirmDialog
      :model-value="removeTarget !== null"
      title="Remove member?"
      :message="`${removeTarget?.fullName} will lose access to this project.`"
      confirm-label="Remove"
      danger
      :loading="removeMemberMutation.isPending.value"
      @update:model-value="removeTarget = null"
      @confirm="removeMemberMutation.mutate(removeTarget!.userId)"
    />
  </div>
</template>
