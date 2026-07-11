<script setup lang="ts">
import { ref } from 'vue'
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../api/client'
import { useAuthStore } from '../../stores/auth'
import { useThemeStore } from '../../stores/theme'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseInput from '../../components/ui/BaseInput.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'

const auth = useAuthStore()
const theme = useThemeStore()
const toast = useToastStore()
const queryClient = useQueryClient()

// ---------- Telegram linking ----------
interface LinkStatus { linked: boolean; telegramUsername: string | null; linkedAt: string | null }
interface GeneratedCode { code: string; expiresInMinutes: number; botConfigured: boolean; mode: string }

const linkQuery = useQuery({
  queryKey: ['telegram', 'link'],
  queryFn: () => api<LinkStatus>('/api/telegram/link'),
})
const generatedCode = ref<GeneratedCode | null>(null)
const generateMutation = useMutation({
  mutationFn: () => api<GeneratedCode>('/api/telegram/link-code', { method: 'POST' }),
  onSuccess: (result) => {
    generatedCode.value = result
    if (!result.botConfigured) toast.info('The bot is not configured yet — ask an administrator.')
  },
  onError: (error) =>
    toast.error(error instanceof ApiError && error.code === 'rate_limited'
      ? 'Too many codes requested. Try again later.'
      : 'Could not generate a code.'),
})
const unlinkConfirm = ref(false)
const unlinkMutation = useMutation({
  mutationFn: () => api('/api/telegram/link', { method: 'DELETE' }),
  onSuccess: () => {
    toast.success('Telegram account unlinked.')
    unlinkConfirm.value = false
    generatedCode.value = null
    queryClient.invalidateQueries({ queryKey: ['telegram', 'link'] })
  },
  onError: () => toast.error('Could not unlink.'),
})

const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const fieldErrors = ref<Record<string, string>>({})

async function changePassword() {
  fieldErrors.value = {}
  if (newPassword.value !== confirmPassword.value) {
    fieldErrors.value.confirm = 'Passwords do not match.'
    return
  }
  loading.value = true
  try {
    await auth.changePassword(currentPassword.value, newPassword.value)
    toast.success('Password updated.')
    currentPassword.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
  } catch (error: any) {
    if (error.errors?.newPassword?.[0]) fieldErrors.value.newPassword = error.errors.newPassword[0]
    else if (error.code === 'invalid_credentials') fieldErrors.value.currentPassword = 'Current password is incorrect.'
    else toast.error('Password change failed.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="flex max-w-md flex-col gap-6">
    <div>
      <h1 class="text-lg font-semibold text-text">Settings</h1>
      <p class="text-sm text-muted">{{ auth.user?.fullName }} · {{ auth.user?.email }}</p>
    </div>

    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-4 text-sm font-semibold text-text">Change password</h2>
      <form class="flex flex-col gap-4" @submit.prevent="changePassword">
        <BaseInput
          v-model="currentPassword"
          label="Current password"
          type="password"
          autocomplete="current-password"
          :error="fieldErrors.currentPassword"
          required
        />
        <BaseInput
          v-model="newPassword"
          label="New password"
          type="password"
          autocomplete="new-password"
          placeholder="At least 10 characters"
          :error="fieldErrors.newPassword"
          required
        />
        <BaseInput
          v-model="confirmPassword"
          label="Confirm new password"
          type="password"
          autocomplete="new-password"
          :error="fieldErrors.confirm"
          required
        />
        <div>
          <BaseButton
            type="submit"
            :loading="loading"
            :disabled="!currentPassword || newPassword.length < 10"
          >
            Update Password
          </BaseButton>
        </div>
      </form>
    </section>

    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">Telegram</h2>
      <p class="mb-4 text-sm text-muted">Link your Telegram account to get review notifications and open the Mini App.</p>

      <div v-if="linkQuery.data.value?.linked" class="flex flex-col gap-3">
        <div class="flex items-center gap-2 rounded-md border border-success/40 bg-success-soft px-3 py-2 text-sm text-success">
          ✓ Linked<span v-if="linkQuery.data.value.telegramUsername"> to @{{ linkQuery.data.value.telegramUsername }}</span>
        </div>
        <BaseButton variant="danger" class="w-fit" @click="unlinkConfirm = true">Unlink</BaseButton>
      </div>

      <div v-else class="flex flex-col gap-3">
        <BaseButton variant="secondary" class="w-fit" :loading="generateMutation.isPending.value" @click="generateMutation.mutate()">
          Generate linking code
        </BaseButton>
        <div v-if="generatedCode" class="rounded-md border border-border bg-bg p-3">
          <p class="text-sm text-text">Open the bot on Telegram and send:</p>
          <p class="my-2 select-all font-mono text-lg font-semibold text-primary">/start {{ generatedCode.code }}</p>
          <p class="text-xs text-muted">Expires in {{ generatedCode.expiresInMinutes }} minutes. This code works once.</p>
        </div>
      </div>
    </section>

    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-2 text-sm font-semibold text-text">Appearance</h2>
      <BaseButton variant="secondary" @click="theme.toggle()">
        {{ theme.isDark ? '☀ Switch to light mode' : '🌙 Switch to dark mode' }}
      </BaseButton>
    </section>

    <ConfirmDialog
      v-model="unlinkConfirm"
      title="Unlink Telegram?"
      message="You will stop receiving notifications and can no longer open the Mini App until you link again."
      confirm-label="Unlink"
      danger
      :loading="unlinkMutation.isPending.value"
      @confirm="unlinkMutation.mutate()"
    />
  </div>
</template>
