<script setup lang="ts">
withDefaults(defineProps<{ rows?: number }>(), { rows: 5 })
</script>

<template>
  <div class="flex flex-col gap-2" aria-busy="true" aria-label="Loading">
    <div
      v-for="i in rows"
      :key="i"
      class="h-11 animate-pulse rounded-md bg-border/60"
    />
  </div>
</template>
