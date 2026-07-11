<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { bootMiniApp } from '../telegram/miniApp'
import SkeletonRows from '../components/ui/SkeletonRows.vue'

const router = useRouter()
const state = ref<'loading' | 'not-linked' | 'not-telegram' | 'failed'>('loading')

onMounted(async () => {
  const boot = await bootMiniApp()
  if (boot.status === 'authenticated') {
    // startapp payloads: r_<recordId> → focus review; p_<projectId> → project queue.
    const param = boot.startParam ?? ''
    if (param.startsWith('r_')) {
      // We only have the record id; the reviewer's queue resolves the project, so send to tasks.
      router.replace({ name: 'review-home' })
    } else {
      router.replace({ name: 'review-home' })
    }
    return
  }
  state.value = boot.status
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-bg p-6">
    <div class="w-full max-w-sm text-center">
      <SkeletonRows v-if="state === 'loading'" :rows="4" />

      <div v-else-if="state === 'not-linked'" class="flex flex-col gap-3">
        <div class="text-3xl">🔗</div>
        <h1 class="text-base font-semibold text-text">Account not linked</h1>
        <p class="text-sm text-muted">
          Open Structura in your browser, go to Settings → Telegram, generate a linking code,
          and send it to this bot as <code>/start YOURCODE</code>. Then reopen this app.
        </p>
      </div>

      <div v-else-if="state === 'not-telegram'" class="flex flex-col gap-3">
        <div class="text-3xl">📱</div>
        <h1 class="text-base font-semibold text-text">Open from Telegram</h1>
        <p class="text-sm text-muted">This page is the Structura Telegram Mini App. Open it from the bot.</p>
        <RouterLink :to="{ name: 'login' }" class="text-sm text-primary underline-offset-2 hover:underline">
          Go to the web sign-in →
        </RouterLink>
      </div>

      <div v-else class="flex flex-col gap-3">
        <div class="text-3xl">⚠️</div>
        <h1 class="text-base font-semibold text-text">Could not sign in</h1>
        <p class="text-sm text-muted">Something went wrong. Please reopen the app from Telegram.</p>
      </div>
    </div>
  </div>
</template>
