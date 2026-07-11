<script setup lang="ts">
defineProps<{ title: string; description?: string }>()
</script>

<template>
  <div class="flex flex-col items-center justify-center gap-2 rounded-xl border border-dashed border-border bg-surface px-6 py-14 text-center">
    <div class="text-3xl" aria-hidden="true"><slot name="icon">📄</slot></div>
    <h3 class="text-sm font-semibold text-text">{{ title }}</h3>
    <p v-if="description" class="max-w-md text-sm text-muted">{{ description }}</p>
    <div v-if="$slots.action" class="mt-3"><slot name="action" /></div>
  </div>
</template>
