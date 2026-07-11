<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { useThemeStore } from '../stores/theme'

const auth = useAuthStore()
const theme = useThemeStore()
const router = useRouter()

const navItems = computed(() => {
  if (auth.isReviewer) {
    return [
      { label: 'My Tasks', to: { name: 'review-home' }, icon: '🗂' },
      { label: 'Progress', to: { name: 'review-progress' }, icon: '📈' },
      { label: 'Settings', to: { name: 'review-settings' }, icon: '⚙️' },
    ]
  }
  const items = [{ label: 'Projects', to: { name: 'projects' }, icon: '📁' }]
  if (auth.isAdministrator) {
    items.push({ label: 'Users', to: { name: 'users' }, icon: '👥' })
    items.push({ label: 'Settings', to: { name: 'admin-settings' }, icon: '⚙️' })
  }
  return items
})

async function signOut() {
  await auth.logout()
  router.push({ name: 'login' })
}
</script>

<template>
  <div class="flex h-full flex-col md:flex-row">
    <!-- Sidebar (top bar on small screens) -->
    <aside class="flex shrink-0 items-center gap-1 border-b border-border bg-surface px-3 py-2 md:h-full md:w-56 md:flex-col md:items-stretch md:border-b-0 md:border-r md:px-3 md:py-4">
      <div class="mr-3 flex items-center gap-2 px-1 md:mb-6 md:mr-0">
        <span class="flex h-7 w-7 items-center justify-center rounded-md bg-primary text-sm font-bold text-white">S</span>
        <span class="text-sm font-semibold tracking-tight text-text">Structura</span>
      </div>
      <nav class="flex flex-1 gap-1 md:flex-col">
        <RouterLink
          v-for="item in navItems"
          :key="item.label"
          :to="item.to"
          class="flex items-center gap-2 rounded-md px-3 py-2 text-sm text-muted hover:bg-bg hover:text-text"
          active-class="bg-primary-soft !text-primary font-medium"
        >
          <span aria-hidden="true">{{ item.icon }}</span>{{ item.label }}
        </RouterLink>
      </nav>
      <div class="flex items-center gap-2 md:flex-col md:items-stretch">
        <button
          class="rounded-md px-3 py-2 text-left text-sm text-muted hover:bg-bg hover:text-text"
          @click="theme.toggle()"
        >
          {{ theme.isDark ? '☀ Light mode' : '🌙 Dark mode' }}
        </button>
        <div class="hidden border-t border-border pt-2 md:block">
          <p class="truncate px-3 text-xs font-medium text-text">{{ auth.user?.fullName }}</p>
          <p class="truncate px-3 text-xs text-muted">{{ auth.user?.email }}</p>
        </div>
        <button
          class="rounded-md px-3 py-2 text-left text-sm text-muted hover:bg-bg hover:text-danger"
          @click="signOut"
        >
          Sign out
        </button>
      </div>
    </aside>

    <main class="flex-1 overflow-y-auto">
      <div class="mx-auto max-w-5xl p-4 md:p-8">
        <RouterView />
      </div>
    </main>
  </div>
</template>
