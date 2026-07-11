<script setup lang="ts">
import { ref, watch } from 'vue'
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../api/client'
import { useToastStore } from '../../stores/toast'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseInput from '../../components/ui/BaseInput.vue'
import BaseSelect from '../../components/ui/BaseSelect.vue'
import BaseBadge from '../../components/ui/BaseBadge.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'

interface Settings {
  publicBaseUrl: string
  telegramMode: string
  telegramBotTokenMasked: string
  telegramConfigured: boolean
}

const toast = useToastStore()
const queryClient = useQueryClient()

const { data, isPending } = useQuery({
  queryKey: ['settings'],
  queryFn: () => api<Settings>('/api/settings'),
})

const form = ref({ publicBaseUrl: '', telegramMode: 'polling', telegramBotToken: '' })
const replacingToken = ref(false)
watch(
  () => data.value,
  (s) => {
    if (!s) return
    form.value = { publicBaseUrl: s.publicBaseUrl, telegramMode: s.telegramMode, telegramBotToken: '' }
    replacingToken.value = !s.telegramConfigured
  },
  { immediate: true },
)

const saveMutation = useMutation({
  mutationFn: () =>
    api('/api/settings', {
      method: 'PUT',
      body: {
        publicBaseUrl: form.value.publicBaseUrl || null,
        telegramMode: form.value.telegramMode,
        telegramBotToken: form.value.telegramBotToken || null,
      },
    }),
  onSuccess: () => {
    toast.success('Settings saved.')
    form.value.telegramBotToken = ''
    replacingToken.value = false
    queryClient.invalidateQueries({ queryKey: ['settings'] })
  },
  onError: (error) =>
    toast.error(error instanceof ApiError && error.detail ? error.detail : 'Could not save settings.'),
})

interface TestResult { ok: boolean; username: string | null; error: string | null }
const testResult = ref<TestResult | null>(null)
const testMutation = useMutation({
  mutationFn: () => api<TestResult>('/api/settings/telegram/test', { method: 'POST' }),
  onSuccess: (r) => (testResult.value = r),
  onError: (error) => toast.error(error instanceof ApiError && error.detail ? error.detail : 'Test failed.'),
})

interface WebhookResult { ok: boolean; webhookUrl: string; error: string | null }
const webhookResult = ref<WebhookResult | null>(null)
const webhookMutation = useMutation({
  mutationFn: () => api<WebhookResult>('/api/settings/telegram/set-webhook', { method: 'POST' }),
  onSuccess: (r) => {
    webhookResult.value = r
    if (r.ok) toast.success('Webhook set.')
    else toast.error(r.error ?? 'Set webhook failed.')
  },
  onError: (error) => toast.error(error instanceof ApiError && error.detail ? error.detail : 'Set webhook failed.'),
})

const modeOptions = [
  { value: 'polling', label: 'Polling (dev / restricted networks)' },
  { value: 'webhook', label: 'Webhook (production, public HTTPS)' },
]
</script>

<template>
  <div class="flex max-w-2xl flex-col gap-6">
    <div>
      <h1 class="text-lg font-semibold text-text">Settings</h1>
      <p class="text-sm text-muted">Installation-wide configuration.</p>
    </div>

    <SkeletonRows v-if="isPending" :rows="5" />

    <template v-else>
      <section class="rounded-xl border border-border bg-surface p-5">
        <h2 class="mb-4 text-sm font-semibold text-text">General</h2>
        <BaseInput v-model="form.publicBaseUrl" label="Public base URL"
          placeholder="https://structura.example.com" />
        <p class="mt-1 text-xs text-muted">Used for Telegram webhook and Mini App / notification links.</p>
      </section>

      <section class="rounded-xl border border-border bg-surface p-5">
        <div class="mb-1 flex items-center justify-between">
          <h2 class="text-sm font-semibold text-text">Telegram bot</h2>
          <BaseBadge :variant="data?.telegramConfigured ? 'success' : 'neutral'">
            {{ data?.telegramConfigured ? 'Configured' : 'Not configured' }}
          </BaseBadge>
        </div>
        <p class="mb-4 text-sm text-muted">Create a bot with @BotFather and paste its token here.</p>
        <div class="flex flex-col gap-4">
          <BaseSelect v-model="form.telegramMode" label="Update mode" :options="modeOptions" />
          <div v-if="data?.telegramConfigured && !replacingToken" class="flex items-end gap-2">
            <div class="flex-1"><BaseInput :model-value="data.telegramBotTokenMasked" label="Bot token" disabled /></div>
            <BaseButton variant="secondary" @click="replacingToken = true">Replace</BaseButton>
          </div>
          <BaseInput v-else v-model="form.telegramBotToken" label="Bot token" type="password"
            autocomplete="off" placeholder="123456:ABC-DEF..." />
        </div>
        <div class="mt-4 flex flex-wrap gap-2">
          <BaseButton :loading="saveMutation.isPending.value" @click="saveMutation.mutate()">Save</BaseButton>
          <BaseButton variant="secondary" :disabled="!data?.telegramConfigured"
            :loading="testMutation.isPending.value" @click="testMutation.mutate()">Test bot</BaseButton>
          <BaseButton v-if="form.telegramMode === 'webhook'" variant="secondary" :disabled="!data?.telegramConfigured"
            :loading="webhookMutation.isPending.value" @click="webhookMutation.mutate()">Set webhook</BaseButton>
        </div>
        <div v-if="testResult" class="mt-3 rounded-md border px-3 py-2 text-sm"
          :class="testResult.ok ? 'border-success/40 bg-success-soft text-success' : 'border-danger/40 bg-danger-soft text-danger'">
          <template v-if="testResult.ok">✓ Connected as @{{ testResult.username }}</template>
          <template v-else>✕ {{ testResult.error }}</template>
        </div>
        <div v-if="webhookResult" class="mt-3 rounded-md border px-3 py-2 text-xs"
          :class="webhookResult.ok ? 'border-success/40 bg-success-soft text-success' : 'border-danger/40 bg-danger-soft text-danger'">
          <template v-if="webhookResult.ok">✓ Webhook set: {{ webhookResult.webhookUrl }}</template>
          <template v-else>✕ {{ webhookResult.error }}</template>
        </div>
      </section>
    </template>
  </div>
</template>
