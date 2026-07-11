import { defineStore } from 'pinia'

type Theme = 'light' | 'dark' | 'system'

const THEME_KEY = 'structura.theme'

function systemPrefersDark(): boolean {
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

export const useThemeStore = defineStore('theme', {
  state: () => ({ theme: (localStorage.getItem(THEME_KEY) as Theme) ?? 'system' }),
  getters: {
    isDark: (s) => (s.theme === 'system' ? systemPrefersDark() : s.theme === 'dark'),
  },
  actions: {
    apply() {
      document.documentElement.setAttribute('data-theme', this.isDark ? 'dark' : 'light')
    },
    set(theme: Theme) {
      this.theme = theme
      localStorage.setItem(THEME_KEY, theme)
      this.apply()
    },
    toggle() {
      this.set(this.isDark ? 'light' : 'dark')
    },
  },
})
