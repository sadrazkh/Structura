import { defineStore } from 'pinia'

export type ToastKind = 'success' | 'error' | 'info' | 'warning'

export interface Toast {
  id: number
  kind: ToastKind
  message: string
}

let nextId = 1

export const useToastStore = defineStore('toast', {
  state: () => ({ toasts: [] as Toast[] }),
  actions: {
    show(kind: ToastKind, message: string) {
      const id = nextId++
      this.toasts.push({ id, kind, message })
      setTimeout(() => this.dismiss(id), 4500)
    },
    success(message: string) { this.show('success', message) },
    error(message: string) { this.show('error', message) },
    info(message: string) { this.show('info', message) },
    dismiss(id: number) {
      this.toasts = this.toasts.filter((t) => t.id !== id)
    },
  },
})
