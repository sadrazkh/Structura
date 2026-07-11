<script setup lang="ts">
import { watch } from 'vue'

const props = defineProps<{ title: string }>()
const open = defineModel<boolean>({ default: false })

watch(open, (isOpen) => {
  document.body.style.overflow = isOpen ? 'hidden' : ''
})

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') open.value = false
}

void props
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="fixed inset-0 z-50 flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      @keydown="onKeydown"
    >
      <div class="absolute inset-0 bg-black/50" @click="open = false" />
      <div class="relative w-full max-w-lg rounded-xl border border-border bg-raised p-5 shadow-xl">
        <div class="mb-4 flex items-center justify-between">
          <h2 class="text-base font-semibold text-text">{{ title }}</h2>
          <button
            class="rounded p-1 text-muted hover:bg-bg hover:text-text"
            aria-label="Close dialog"
            @click="open = false"
          >
            ✕
          </button>
        </div>
        <slot />
        <div v-if="$slots.footer" class="mt-5 flex justify-end gap-2">
          <slot name="footer" />
        </div>
      </div>
    </div>
  </Teleport>
</template>
