<script setup lang="ts">
import ToastList from './components/ui/ToastList.vue'
import BaseButton from './components/ui/BaseButton.vue'
import { pwaNeedsRefresh, applyPwaUpdate } from './pwa/usePwa'
</script>

<template>
  <RouterView />
  <ToastList />

  <!-- PWA update prompt -->
  <div
    v-if="pwaNeedsRefresh"
    class="fixed bottom-4 left-1/2 z-[70] flex -translate-x-1/2 items-center gap-3 rounded-xl border border-border bg-raised px-4 py-3 shadow-xl"
  >
    <span class="text-sm text-text">A new version is available.</span>
    <BaseButton class="!py-1" @click="applyPwaUpdate">Refresh</BaseButton>
    <button class="text-sm text-muted hover:text-text" @click="pwaNeedsRefresh = false">Later</button>
  </div>
</template>
