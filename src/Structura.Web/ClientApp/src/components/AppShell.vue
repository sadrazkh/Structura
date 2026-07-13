<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { useThemeStore } from '../stores/theme'

const auth = useAuthStore()
const theme = useThemeStore()
const router = useRouter()
const route = useRoute()

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

// Focus Review is an immersive one-record surface with its own bottom action bar;
// hide the global bottom tab bar there so the two never collide.
const hideBottomNav = computed(() => route.name === 'review-focus')

async function signOut() {
  await auth.logout()
  router.push({ name: 'login' })
}
</script>

<template>
  <div class="flex h-full flex-col md:flex-row">
    <!-- Mobile top bar (brand + icon actions) -->
    <header class="flex shrink-0 items-center gap-2 border-b border-border bg-surface px-3 py-2.5 md:hidden">
      <span class="flex h-7 w-7 items-center justify-center rounded-md bg-primary text-sm font-bold text-white">S</span>
      <span class="text-sm font-semibold tracking-tight text-text">Structura</span>
      <button
        class="ml-auto rounded-md p-2 text-muted hover:bg-bg hover:text-text"
        :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
        @click="theme.toggle()"
      >
        {{ theme.isDark ? '☀' : '🌙' }}
      </button>
      <button
        class="rounded-md p-2 text-muted hover:bg-bg hover:text-danger"
        aria-label="Sign out"
        @click="signOut"
      >
        ⎋
      </button>
    </header>

    <!-- Desktop left sidebar -->
    <aside class="hidden shrink-0 flex-col border-r border-border bg-surface px-3 py-4 md:flex md:h-full md:w-56">
      <div class="mb-6 flex items-center gap-2 px-1">
        <span class="flex h-7 w-7 items-center justify-center rounded-md bg-primary text-sm font-bold text-white">S</span>
        <span class="text-sm font-semibold tracking-tight text-text">Structura</span>
      </div>
      <nav class="flex flex-1 flex-col gap-1">
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
      <div class="flex flex-col items-stretch gap-1">
        <button
          class="rounded-md px-3 py-2 text-left text-sm text-muted hover:bg-bg hover:text-text"
          @click="theme.toggle()"
        >
          {{ theme.isDark ? '☀ Light mode' : '🌙 Dark mode' }}
        </button>
        <div class="border-t border-border pt-2">
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
      <div class="mx-auto max-w-5xl p-4 pb-24 md:p-8 md:pb-8">
        <RouterView />
      </div>
    </main>

    <!-- Mobile bottom tab bar -->
    <nav
      v-if="!hideBottomNav"
      class="fixed inset-x-0 bottom-0 z-40 flex border-t border-border bg-surface md:hidden"
      style="padding-bottom: env(safe-area-inset-bottom)"
    >
      <RouterLink
        v-for="item in navItems"
        :key="item.label"
        :to="item.to"
        class="flex flex-1 flex-col items-center gap-0.5 py-2 text-xs text-muted"
        active-class="!text-primary"
      >
        <span class="text-lg leading-none" aria-hidden="true">{{ item.icon }}</span>
        {{ item.label }}
      </RouterLink>
    </nav>
  </div>
</template>
