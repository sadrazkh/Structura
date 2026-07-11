<script setup lang="ts">
import { ref } from 'vue'
import { useAuthStore } from '../../stores/auth'
import { useThemeStore } from '../../stores/theme'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseInput from '../../components/ui/BaseInput.vue'

const auth = useAuthStore()
const theme = useThemeStore()
const toast = useToastStore()

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
      <h2 class="mb-2 text-sm font-semibold text-text">Appearance</h2>
      <BaseButton variant="secondary" @click="theme.toggle()">
        {{ theme.isDark ? '☀ Switch to light mode' : '🌙 Switch to dark mode' }}
      </BaseButton>
    </section>
  </div>
</template>
