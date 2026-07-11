<script setup lang="ts">
import { useToastStore } from '../../stores/toast'

const store = useToastStore()

const styles: Record<string, string> = {
  success: 'border-success/40 bg-success-soft text-success',
  error: 'border-danger/40 bg-danger-soft text-danger',
  warning: 'border-warning/40 bg-warning-soft text-warning',
  info: 'border-info/40 bg-info-soft text-info',
}
</script>

<template>
  <div class="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-80 flex-col gap-2" aria-live="polite">
    <div
      v-for="toast in store.toasts"
      :key="toast.id"
      class="pointer-events-auto flex items-start justify-between gap-3 rounded-lg border px-3.5 py-2.5 text-sm shadow-lg"
      :class="styles[toast.kind]"
    >
      <span>{{ toast.message }}</span>
      <button class="opacity-60 hover:opacity-100" aria-label="Dismiss" @click="store.dismiss(toast.id)">✕</button>
    </div>
  </div>
</template>
