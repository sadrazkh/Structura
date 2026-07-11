import { ref } from 'vue'
import { registerSW } from 'virtual:pwa-register'

/** SW registration + update prompt. Disabled inside the Telegram Mini App (/tg). */
export const pwaNeedsRefresh = ref(false)

let updateFn: ((reload?: boolean) => Promise<void>) | null = null

export function initPwa() {
  if (window.location.pathname.startsWith('/tg')) return // Mini App runs inside Telegram; no SW.
  if (!('serviceWorker' in navigator)) return
  updateFn = registerSW({
    immediate: true,
    onNeedRefresh() {
      pwaNeedsRefresh.value = true
    },
  })
}

export async function applyPwaUpdate() {
  pwaNeedsRefresh.value = false
  if (updateFn) await updateFn(true)
}
