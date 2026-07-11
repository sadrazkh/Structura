<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../api/client'
import { useAuthStore } from '../../stores/auth'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseInput from '../../components/ui/BaseInput.vue'
import BaseSelect from '../../components/ui/BaseSelect.vue'
import BaseDialog from '../../components/ui/BaseDialog.vue'
import BaseBadge from '../../components/ui/BaseBadge.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import EmptyState from '../../components/ui/EmptyState.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface UserItem {
  id: string
  fullName: string
  email: string
  role: string
  isActive: boolean
  mustChangePassword: boolean
  lastLoginAt: string | null
  createdAt: string
}

const auth = useAuthStore()
const toast = useToastStore()
const queryClient = useQueryClient()

const search = ref('')
const { data, isPending, isError, refetch } = useQuery({
  queryKey: ['users'],
  queryFn: () => api<{ items: UserItem[] }>('/api/users'),
})

const filtered = computed(() => {
  const term = search.value.trim().toLowerCase()
  const items = data.value?.items ?? []
  if (!term) return items
  return items.filter(
    (u) => u.fullName.toLowerCase().includes(term) || u.email.toLowerCase().includes(term),
  )
})

const roleOptions = [
  { value: 'Reviewer', label: 'Reviewer' },
  { value: 'ProjectManager', label: 'Project Manager' },
  { value: 'Administrator', label: 'Administrator' },
]

function generatePassword(): string {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%'
  const bytes = crypto.getRandomValues(new Uint8Array(14))
  return Array.from(bytes, (b) => chars[b % chars.length]).join('')
}

function invalidate() {
  queryClient.invalidateQueries({ queryKey: ['users'] })
}

function apiErrorToast(fallback: string) {
  return (error: unknown) => {
    if (error instanceof ApiError && error.code === 'last_administrator') {
      toast.error('This is the last active administrator — the action was blocked.')
    } else if (error instanceof ApiError && error.detail) {
      toast.error(error.detail)
    } else {
      toast.error(fallback)
    }
  }
}

// --- Create ---
const createOpen = ref(false)
const form = ref({ fullName: '', email: '', password: '', role: 'Reviewer' })
const formErrors = ref<Record<string, string>>({})

const createMutation = useMutation({
  mutationFn: () => api('/api/users', { method: 'POST', body: form.value }),
  onSuccess: () => {
    toast.success(`User created. They must change this password at first sign-in.`)
    createOpen.value = false
    invalidate()
  },
  onError: (error) => {
    formErrors.value = {}
    if (error instanceof ApiError && error.code === 'duplicate') {
      formErrors.value.email = 'A user with this email already exists.'
    } else if (error instanceof ApiError && error.errors) {
      for (const [field, messages] of Object.entries(error.errors)) formErrors.value[field] = messages[0]
    } else {
      toast.error('Could not create the user.')
    }
  },
})

function openCreate() {
  form.value = { fullName: '', email: '', password: generatePassword(), role: 'Reviewer' }
  formErrors.value = {}
  createOpen.value = true
}

// --- Edit ---
const editTarget = ref<UserItem | null>(null)
const editForm = ref({ fullName: '', role: 'Reviewer' })

const editMutation = useMutation({
  mutationFn: () =>
    api(`/api/users/${editTarget.value!.id}`, { method: 'PUT', body: editForm.value }),
  onSuccess: () => {
    toast.success('User updated.')
    editTarget.value = null
    invalidate()
  },
  onError: apiErrorToast('Could not update the user.'),
})

function openEdit(user: UserItem) {
  editTarget.value = user
  editForm.value = { fullName: user.fullName, role: user.role }
}

// --- Reset password ---
const resetTarget = ref<UserItem | null>(null)
const resetPassword = ref('')

const resetMutation = useMutation({
  mutationFn: () =>
    api(`/api/users/${resetTarget.value!.id}/reset-password`, {
      method: 'POST',
      body: { newPassword: resetPassword.value },
    }),
  onSuccess: () => {
    toast.success('Password reset. The user must change it at next sign-in.')
    resetTarget.value = null
    invalidate()
  },
  onError: apiErrorToast('Could not reset the password.'),
})

function openReset(user: UserItem) {
  resetTarget.value = user
  resetPassword.value = generatePassword()
}

// --- Activate / deactivate ---
const deactivateTarget = ref<UserItem | null>(null)
const deactivateMutation = useMutation({
  mutationFn: (user: UserItem) => api(`/api/users/${user.id}/deactivate`, { method: 'POST' }),
  onSuccess: () => {
    toast.success('User deactivated.')
    deactivateTarget.value = null
    invalidate()
  },
  onError: (error) => {
    deactivateTarget.value = null
    apiErrorToast('Could not deactivate the user.')(error)
  },
})
const reactivateMutation = useMutation({
  mutationFn: (user: UserItem) => api(`/api/users/${user.id}/reactivate`, { method: 'POST' }),
  onSuccess: () => {
    toast.success('User reactivated.')
    invalidate()
  },
  onError: apiErrorToast('Could not reactivate the user.'),
})

const roleBadge: Record<string, 'primary' | 'info' | 'neutral'> = {
  Administrator: 'primary',
  ProjectManager: 'info',
  Reviewer: 'neutral',
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h1 class="text-lg font-semibold text-text">Users</h1>
        <p class="text-sm text-muted">Create accounts and control who can sign in.</p>
      </div>
      <BaseButton @click="openCreate">+ New User</BaseButton>
    </div>

    <div class="max-w-xs">
      <BaseInput v-model="search" placeholder="Search by name or email…" />
    </div>

    <SkeletonRows v-if="isPending" :rows="5" />

    <EmptyState v-else-if="isError" title="Could not load users" description="Check your connection and try again.">
      <template #icon>⚠️</template>
      <template #action><BaseButton variant="secondary" @click="refetch()">Try again</BaseButton></template>
    </EmptyState>

    <EmptyState v-else-if="filtered.length === 0" title="No users found" description="Adjust your search or create a new user.">
      <template #icon>👥</template>
    </EmptyState>

    <div v-else class="overflow-x-auto rounded-xl border border-border bg-surface">
      <table class="w-full min-w-[640px] text-left text-sm">
        <thead>
          <tr class="border-b border-border text-xs uppercase tracking-wide text-muted">
            <th class="px-4 py-3 font-medium">Name</th>
            <th class="px-4 py-3 font-medium">Role</th>
            <th class="px-4 py-3 font-medium">Status</th>
            <th class="px-4 py-3 font-medium">Last sign-in</th>
            <th class="px-4 py-3 font-medium"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody class="divide-y divide-border">
          <tr v-for="user in filtered" :key="user.id" :class="{ 'opacity-60': !user.isActive }">
            <td class="px-4 py-3">
              <p class="font-medium text-text">{{ user.fullName }}</p>
              <p class="text-xs text-muted">{{ user.email }}</p>
            </td>
            <td class="px-4 py-3">
              <BaseBadge :variant="roleBadge[user.role] ?? 'neutral'">{{ user.role }}</BaseBadge>
            </td>
            <td class="px-4 py-3">
              <BaseBadge :variant="user.isActive ? 'success' : 'danger'">
                {{ user.isActive ? 'Active' : 'Deactivated' }}
              </BaseBadge>
              <BaseBadge v-if="user.mustChangePassword && user.isActive" variant="warning" class="ml-1">
                Pending first sign-in
              </BaseBadge>
            </td>
            <td class="px-4 py-3 text-muted">
              {{ user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : '—' }}
            </td>
            <td class="px-4 py-3">
              <div class="flex justify-end gap-1">
                <BaseButton variant="ghost" class="!px-2 !py-1" @click="openEdit(user)">Edit</BaseButton>
                <BaseButton variant="ghost" class="!px-2 !py-1" @click="openReset(user)">Reset password</BaseButton>
                <BaseButton
                  v-if="user.isActive && user.id !== auth.user?.id"
                  variant="ghost"
                  class="!px-2 !py-1 text-danger"
                  @click="deactivateTarget = user"
                >
                  Deactivate
                </BaseButton>
                <BaseButton
                  v-else-if="!user.isActive"
                  variant="ghost"
                  class="!px-2 !py-1"
                  :loading="reactivateMutation.isPending.value"
                  @click="reactivateMutation.mutate(user)"
                >
                  Reactivate
                </BaseButton>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- Create dialog -->
    <BaseDialog v-model="createOpen" title="New User">
      <form class="flex flex-col gap-4" @submit.prevent="createMutation.mutate()">
        <BaseInput v-model="form.fullName" label="Full name" :error="formErrors.fullName" required />
        <BaseInput v-model="form.email" label="Email" type="email" :error="formErrors.email" required />
        <BaseSelect v-model="form.role" label="Role" :options="roleOptions" required />
        <div class="flex items-end gap-2">
          <div class="flex-1">
            <BaseInput v-model="form.password" label="Initial password" :error="formErrors.password" required />
          </div>
          <BaseButton variant="secondary" @click="form.password = generatePassword()">Generate</BaseButton>
        </div>
        <p class="text-xs text-muted">
          Share this password with the user securely — they must replace it at first sign-in.
        </p>
      </form>
      <template #footer>
        <BaseButton variant="secondary" @click="createOpen = false">Cancel</BaseButton>
        <BaseButton
          :loading="createMutation.isPending.value"
          :disabled="!form.fullName.trim() || !form.email.trim() || !form.password"
          @click="createMutation.mutate()"
        >
          Create User
        </BaseButton>
      </template>
    </BaseDialog>

    <!-- Edit dialog -->
    <BaseDialog :model-value="editTarget !== null" title="Edit User" @update:model-value="editTarget = null">
      <form class="flex flex-col gap-4" @submit.prevent="editMutation.mutate()">
        <BaseInput v-model="editForm.fullName" label="Full name" required />
        <BaseSelect v-model="editForm.role" label="Role" :options="roleOptions" required />
      </form>
      <template #footer>
        <BaseButton variant="secondary" @click="editTarget = null">Cancel</BaseButton>
        <BaseButton :loading="editMutation.isPending.value" @click="editMutation.mutate()">Save</BaseButton>
      </template>
    </BaseDialog>

    <!-- Reset password dialog -->
    <BaseDialog :model-value="resetTarget !== null" title="Reset Password" @update:model-value="resetTarget = null">
      <div class="flex flex-col gap-4">
        <p class="text-sm text-muted">
          Set a new temporary password for <strong class="text-text">{{ resetTarget?.fullName }}</strong
          >. Their current sessions will be signed out and they must change it at next sign-in.
        </p>
        <div class="flex items-end gap-2">
          <div class="flex-1">
            <BaseInput v-model="resetPassword" label="New password" required />
          </div>
          <BaseButton variant="secondary" @click="resetPassword = generatePassword()">Generate</BaseButton>
        </div>
      </div>
      <template #footer>
        <BaseButton variant="secondary" @click="resetTarget = null">Cancel</BaseButton>
        <BaseButton
          :loading="resetMutation.isPending.value"
          :disabled="resetPassword.length < 10"
          @click="resetMutation.mutate()"
        >
          Reset Password
        </BaseButton>
      </template>
    </BaseDialog>

    <ConfirmDialog
      :model-value="deactivateTarget !== null"
      title="Deactivate user?"
      :message="`${deactivateTarget?.fullName} will be signed out everywhere and can no longer sign in.`"
      confirm-label="Deactivate"
      danger
      :loading="deactivateMutation.isPending.value"
      @update:model-value="deactivateTarget = null"
      @confirm="deactivateMutation.mutate(deactivateTarget!)"
    />
  </div>
</template>
