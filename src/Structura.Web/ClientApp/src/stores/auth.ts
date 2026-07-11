import { defineStore } from 'pinia'

export interface SessionUser {
  id: string
  fullName: string
  email: string
  role: 'Administrator' | 'ProjectManager' | 'Reviewer'
}

interface AuthResponse {
  accessToken: string
  expiresInSeconds: number
  refreshToken: string
  mustChangePassword: boolean
  user: SessionUser
}

const REFRESH_KEY = 'structura.refreshToken'

async function postJson(path: string, body: unknown): Promise<Response> {
  return fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    accessToken: null as string | null,
    refreshToken: localStorage.getItem(REFRESH_KEY),
    user: null as SessionUser | null,
    mustChangePassword: false,
    /** True once the initial silent-refresh attempt finished (router waits for it). */
    ready: false,
    refreshPromise: null as Promise<boolean> | null,
  }),

  getters: {
    isAuthenticated: (s) => s.accessToken !== null && s.user !== null,
    isAdministrator: (s) => s.user?.role === 'Administrator',
    canManageProjects: (s) => s.user?.role === 'Administrator' || s.user?.role === 'ProjectManager',
    isReviewer: (s) => s.user?.role === 'Reviewer',
  },

  actions: {
    applySession(auth: AuthResponse) {
      this.accessToken = auth.accessToken
      this.refreshToken = auth.refreshToken
      this.user = auth.user
      this.mustChangePassword = auth.mustChangePassword
      localStorage.setItem(REFRESH_KEY, auth.refreshToken)
    },

    clearSession() {
      this.accessToken = null
      this.refreshToken = null
      this.user = null
      this.mustChangePassword = false
      localStorage.removeItem(REFRESH_KEY)
    },

    /** Restores the session from the stored refresh token on app start. */
    async initialize() {
      if (this.ready) return
      if (this.refreshToken) await this.tryRefresh()
      this.ready = true
    },

    /** Single-flight silent refresh; returns whether a new session was obtained. */
    tryRefresh(): Promise<boolean> {
      if (this.refreshPromise) return this.refreshPromise
      this.refreshPromise = (async () => {
        try {
          if (!this.refreshToken) return false
          const response = await postJson('/api/auth/refresh', { refreshToken: this.refreshToken })
          if (!response.ok) {
            this.clearSession()
            return false
          }
          this.applySession((await response.json()) as AuthResponse)
          return true
        } catch {
          return false
        } finally {
          this.refreshPromise = null
        }
      })()
      return this.refreshPromise
    },

    async login(email: string, password: string) {
      const response = await postJson('/api/auth/login', { email, password })
      if (!response.ok) {
        const problem = await response.json().catch(() => ({}))
        throw Object.assign(new Error(problem.detail ?? 'Sign-in failed.'), {
          code: problem.code ?? 'invalid_credentials',
        })
      }
      this.applySession((await response.json()) as AuthResponse)
    },

    async changePassword(currentPassword: string, newPassword: string) {
      const response = await fetch('/api/auth/change-password', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${this.accessToken}`,
        },
        body: JSON.stringify({ currentPassword, newPassword }),
      })
      if (!response.ok) {
        const problem = await response.json().catch(() => ({}))
        throw Object.assign(new Error(problem.detail ?? 'Password change failed.'), {
          code: problem.code ?? 'validation_failed',
          errors: problem.errors,
        })
      }
      this.applySession((await response.json()) as AuthResponse)
    },

    async logout() {
      try {
        if (this.accessToken) {
          await fetch('/api/auth/logout', {
            method: 'POST',
            headers: { Authorization: `Bearer ${this.accessToken}` },
          })
        }
      } finally {
        this.clearSession()
      }
    },

    /** Route name of the landing page for the current role. */
    homeRoute(): string {
      return this.isReviewer ? 'review-home' : 'projects'
    },
  },
})
