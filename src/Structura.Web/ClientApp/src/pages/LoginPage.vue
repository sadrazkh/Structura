<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import BaseButton from '../components/ui/BaseButton.vue'
import BaseInput from '../components/ui/BaseInput.vue'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const email = ref('')
const password = ref('')
const loading = ref(false)
const errorMessage = ref('')

async function submit() {
  if (!email.value || !password.value) {
    errorMessage.value = 'Enter your email and password.'
    return
  }
  loading.value = true
  errorMessage.value = ''
  try {
    await auth.login(email.value.trim(), password.value)
    if (auth.mustChangePassword) {
      router.push({ name: 'change-password' })
    } else {
      const redirect = route.query.redirect as string | undefined
      router.push(redirect ?? { name: auth.homeRoute() })
    }
  } catch (error: any) {
    errorMessage.value =
      error.code === 'account_locked'
        ? 'Account is temporarily locked. Try again in a few minutes.'
        : error.code === 'setup_required'
          ? 'Setup required: no administrator account exists yet. Check the server configuration.'
          : 'Invalid email or password.'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="flex min-h-full items-center justify-center bg-bg p-4">
    <div class="w-full max-w-sm">
      <div class="mb-6 flex items-center justify-center gap-2">
        <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-primary text-lg font-bold text-white">S</span>
        <span class="text-lg font-semibold tracking-tight text-text">Structura</span>
      </div>
      <form
        class="flex flex-col gap-4 rounded-xl border border-border bg-surface p-6 shadow-sm"
        @submit.prevent="submit"
      >
        <h1 class="text-base font-semibold text-text">Sign in</h1>
        <div
          v-if="errorMessage"
          class="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger"
          role="alert"
        >
          {{ errorMessage }}
        </div>
        <BaseInput v-model="email" label="Email" type="email" autocomplete="username" required />
        <BaseInput v-model="password" label="Password" type="password" autocomplete="current-password" required />
        <BaseButton type="submit" :loading="loading" class="mt-1 w-full">Sign in</BaseButton>
      </form>
    </div>
  </div>
</template>
