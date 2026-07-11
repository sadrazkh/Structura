<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { useToastStore } from '../stores/toast'
import BaseButton from '../components/ui/BaseButton.vue'
import BaseInput from '../components/ui/BaseInput.vue'

const auth = useAuthStore()
const toast = useToastStore()
const router = useRouter()

const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const errorMessage = ref('')
const fieldErrors = ref<Record<string, string>>({})

async function submit() {
  fieldErrors.value = {}
  errorMessage.value = ''
  if (newPassword.value !== confirmPassword.value) {
    fieldErrors.value.confirm = 'Passwords do not match.'
    return
  }
  loading.value = true
  try {
    await auth.changePassword(currentPassword.value, newPassword.value)
    toast.success('Password updated.')
    router.push({ name: auth.homeRoute() })
  } catch (error: any) {
    if (error.errors?.newPassword?.[0]) {
      fieldErrors.value.newPassword = error.errors.newPassword[0]
    } else if (error.code === 'invalid_credentials') {
      fieldErrors.value.currentPassword = 'Current password is incorrect.'
    } else {
      errorMessage.value = error.message ?? 'Password change failed.'
    }
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="flex min-h-full items-center justify-center bg-bg p-4">
    <form
      class="flex w-full max-w-sm flex-col gap-4 rounded-xl border border-border bg-surface p-6 shadow-sm"
      @submit.prevent="submit"
    >
      <div>
        <h1 class="text-base font-semibold text-text">Change your password</h1>
        <p v-if="auth.mustChangePassword" class="mt-1 text-sm text-muted">
          You must set a new password before continuing.
        </p>
      </div>
      <div
        v-if="errorMessage"
        class="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger"
        role="alert"
      >
        {{ errorMessage }}
      </div>
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
        :error="fieldErrors.newPassword"
        placeholder="At least 10 characters"
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
      <BaseButton type="submit" :loading="loading" class="mt-1 w-full">Update password</BaseButton>
    </form>
  </div>
</template>
