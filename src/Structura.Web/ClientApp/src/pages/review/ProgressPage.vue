<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { api } from '../../api/client'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface Progress {
  pending: number
  approved: number
  rejected: number
  reprocessRequested: number
  decidedToday: number
}

const { data, isPending } = useQuery({
  queryKey: ['review', 'progress'],
  queryFn: () => api<Progress>('/api/review/progress'),
})

const cards = [
  { key: 'pending', label: 'Waiting for you', icon: '🗂' },
  { key: 'decidedToday', label: 'Decided today', icon: '⚡' },
  { key: 'approved', label: 'Approved (total)', icon: '✅' },
  { key: 'rejected', label: 'Rejected (total)', icon: '✕' },
  { key: 'reprocessRequested', label: 'Sent back (total)', icon: '↺' },
] as const
</script>

<template>
  <div class="flex flex-col gap-5">
    <div>
      <h1 class="text-lg font-semibold text-text">Progress</h1>
      <p class="text-sm text-muted">Your personal review activity.</p>
    </div>

    <SkeletonRows v-if="isPending" :rows="3" />

    <div v-else-if="data" class="grid grid-cols-2 gap-3 sm:grid-cols-3">
      <div
        v-for="card in cards"
        :key="card.key"
        class="rounded-xl border border-border bg-surface p-4"
      >
        <p class="text-2xl font-semibold text-text">
          <span aria-hidden="true" class="mr-1 text-base">{{ card.icon }}</span>{{ data[card.key] }}
        </p>
        <p class="mt-1 text-xs text-muted">{{ card.label }}</p>
      </div>
    </div>
  </div>
</template>
