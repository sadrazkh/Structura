import { useAuthStore } from '../stores/auth'
import { useThemeStore } from '../stores/theme'

interface TelegramWebApp {
  initData: string
  colorScheme: 'light' | 'dark'
  themeParams: Record<string, string>
  ready: () => void
  expand: () => void
  BackButton: { show: () => void; hide: () => void; onClick: (cb: () => void) => void }
}

declare global {
  interface Window {
    Telegram?: { WebApp?: TelegramWebApp }
  }
}

interface AuthResponse {
  accessToken: string
  expiresInSeconds: number
  refreshToken: string
  mustChangePassword: boolean
  user: { id: string; fullName: string; email: string; role: 'Administrator' | 'ProjectManager' | 'Reviewer' }
}

function loadSdk(): Promise<TelegramWebApp | null> {
  return new Promise((resolve) => {
    if (window.Telegram?.WebApp) return resolve(window.Telegram.WebApp)
    const script = document.createElement('script')
    script.src = 'https://telegram.org/js/telegram-web-app.js'
    script.onload = () => resolve(window.Telegram?.WebApp ?? null)
    script.onerror = () => resolve(null)
    document.head.appendChild(script)
  })
}

export interface MiniAppBoot {
  status: 'authenticated' | 'not-telegram' | 'not-linked' | 'failed'
  startParam?: string
}

/**
 * Boots the Reviewer SPA inside Telegram: validates initData for a JWT, maps the Telegram
 * theme onto the design system, and returns the start parameter for deep-linking.
 */
export async function bootMiniApp(): Promise<MiniAppBoot> {
  const webApp = await loadSdk()
  if (!webApp || !webApp.initData) return { status: 'not-telegram' }

  webApp.ready()
  webApp.expand()

  // Map Telegram theme → design-system dark/light.
  const theme = useThemeStore()
  theme.set(webApp.colorScheme === 'dark' ? 'dark' : 'light')

  try {
    const response = await fetch('/api/auth/telegram-miniapp', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ initData: webApp.initData }),
    })
    if (response.status === 401) return { status: 'not-linked' }
    if (!response.ok) return { status: 'failed' }
    const auth = (await response.json()) as AuthResponse
    useAuthStore().applySession(auth)

    // start_param comes from ?startapp=... on the deep link.
    const params = new URLSearchParams(webApp.initData)
    return { status: 'authenticated', startParam: params.get('start_param') ?? undefined }
  } catch {
    return { status: 'failed' }
  }
}
