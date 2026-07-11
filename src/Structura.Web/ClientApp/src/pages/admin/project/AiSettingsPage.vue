<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { useToastStore } from '../../../stores/toast'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseSelect from '../../../components/ui/BaseSelect.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface AiSettings {
  configured: boolean
  provider: 'OpenRouter' | 'Nvidia'
  baseUrl: string
  apiKeyMasked: string
  hasApiKey: boolean
  model: string
  temperature: number
  maxOutputTokens: number
  timeoutSeconds: number
  concurrency: number
  systemInstruction: string
  extractionInstruction: string
}

interface TestResult {
  ok: boolean
  durationMs: number
  inputTokens: number
  outputTokens: number
  sample: string | null
  error?: string
}

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()

const DEFAULT_BASE_URLS: Record<string, string> = {
  OpenRouter: 'https://openrouter.ai/api/v1',
  Nvidia: 'https://integrate.api.nvidia.com/v1',
}

const form = reactive({
  provider: 'OpenRouter',
  baseUrl: DEFAULT_BASE_URLS.OpenRouter,
  apiKey: '',
  model: '',
  temperature: 0.1,
  maxOutputTokens: 2048,
  timeoutSeconds: 60,
  concurrency: 5,
  systemInstruction: '',
  extractionInstruction: '',
})
const hasStoredKey = ref(false)
const maskedKey = ref('')
const replacingKey = ref(false)
const fieldErrors = ref<Record<string, string>>({})
const testResult = ref<TestResult | null>(null)

const settingsQuery = useQuery({
  queryKey: ['project', projectId, 'ai-config'],
  queryFn: () => api<AiSettings>(`/api/projects/${projectId}/ai-config`),
})

watch(
  () => settingsQuery.data.value,
  (data) => {
    if (!data) return
    Object.assign(form, {
      provider: data.provider,
      baseUrl: data.baseUrl,
      apiKey: '',
      model: data.model,
      temperature: data.temperature,
      maxOutputTokens: data.maxOutputTokens,
      timeoutSeconds: data.timeoutSeconds,
      concurrency: data.concurrency,
      systemInstruction: data.systemInstruction,
      extractionInstruction: data.extractionInstruction,
    })
    hasStoredKey.value = data.hasApiKey
    maskedKey.value = data.apiKeyMasked
    replacingKey.value = !data.hasApiKey
  },
  { immediate: true },
)

function onProviderChange(provider: string) {
  form.provider = provider as 'OpenRouter' | 'Nvidia'
  form.baseUrl = DEFAULT_BASE_URLS[provider] ?? form.baseUrl
}

const saveMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}/ai-config`, {
      method: 'PUT',
      body: { ...form, apiKey: form.apiKey || null },
    }),
  onSuccess: () => {
    toast.success('AI settings saved.')
    fieldErrors.value = {}
    form.apiKey = ''
    testResult.value = null
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'ai-config'] })
  },
  onError: (error) => {
    fieldErrors.value = {}
    if (error instanceof ApiError && error.errors) {
      for (const [field, messages] of Object.entries(error.errors)) fieldErrors.value[field] = messages[0]
    } else if (error instanceof ApiError && error.detail) {
      toast.error(error.detail)
    } else {
      toast.error('Could not save AI settings.')
    }
  },
})

const testMutation = useMutation({
  mutationFn: () => api<TestResult>(`/api/projects/${projectId}/ai-config/test`, { method: 'POST' }),
  onSuccess: (result) => (testResult.value = result),
  onError: (error) => {
    testResult.value = null
    toast.error(error instanceof ApiError && error.detail ? error.detail : 'Test failed to run.')
  },
})

const providerOptions = [
  { value: 'OpenRouter', label: 'OpenRouter' },
  { value: 'Nvidia', label: 'NVIDIA (OpenAI-compatible)' },
]
</script>

<template>
  <SkeletonRows v-if="settingsQuery.isPending.value" :rows="6" />

  <div v-else class="flex max-w-2xl flex-col gap-6">
    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">AI Provider</h2>
      <p class="mb-4 text-sm text-muted">
        Record text is sent to this provider's API during processing. Verify its data-use policy
        before processing sensitive data.
      </p>
      <form class="flex flex-col gap-4" @submit.prevent="saveMutation.mutate()">
        <div class="grid gap-4 sm:grid-cols-2">
          <BaseSelect
            :model-value="form.provider"
            label="Provider"
            :options="providerOptions"
            :disabled="isArchived"
            @update:model-value="onProviderChange"
          />
          <BaseInput v-model="form.model" label="Model" required :disabled="isArchived"
            placeholder="e.g. openai/gpt-4.1-mini" :error="fieldErrors.model" />
        </div>
        <BaseInput v-model="form.baseUrl" label="Base URL" required :disabled="isArchived" :error="fieldErrors.baseUrl" />

        <!-- API key: write-only with replace semantics -->
        <div v-if="hasStoredKey && !replacingKey" class="flex items-end gap-2">
          <div class="flex-1">
            <BaseInput :model-value="maskedKey" label="API key" disabled />
          </div>
          <BaseButton variant="secondary" :disabled="isArchived" @click="replacingKey = true">Replace key</BaseButton>
        </div>
        <BaseInput
          v-else
          v-model="form.apiKey"
          label="API key"
          type="password"
          autocomplete="off"
          :required="!hasStoredKey"
          :disabled="isArchived"
          placeholder="Stored encrypted — never shown again"
        />

        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <BaseInput v-model.number="form.temperature as any" label="Temperature" type="number" :disabled="isArchived" :error="fieldErrors.temperature" />
          <BaseInput v-model.number="form.maxOutputTokens as any" label="Max output tokens" type="number" :disabled="isArchived" :error="fieldErrors.maxOutputTokens" />
          <BaseInput v-model.number="form.timeoutSeconds as any" label="Timeout (seconds)" type="number" :disabled="isArchived" :error="fieldErrors.timeoutSeconds" />
          <BaseInput v-model.number="form.concurrency as any" label="Concurrency" type="number" :disabled="isArchived" :error="fieldErrors.concurrency" />
        </div>

        <div class="flex items-center gap-2">
          <BaseButton type="submit" :loading="saveMutation.isPending.value" :disabled="isArchived">
            Save Settings
          </BaseButton>
          <BaseButton
            variant="secondary"
            :loading="testMutation.isPending.value"
            :disabled="!settingsQuery.data.value?.configured"
            @click="testMutation.mutate()"
          >
            Test Connection
          </BaseButton>
        </div>

        <div
          v-if="testResult"
          class="rounded-md border px-3 py-2 text-sm"
          :class="testResult.ok
            ? 'border-success/40 bg-success-soft text-success'
            : 'border-danger/40 bg-danger-soft text-danger'"
        >
          <template v-if="testResult.ok">
            ✓ Connection OK — {{ testResult.durationMs }} ms ·
            {{ testResult.inputTokens }} in / {{ testResult.outputTokens }} out tokens
            <span v-if="testResult.sample"> · reply: “{{ testResult.sample }}”</span>
          </template>
          <template v-else>✕ {{ testResult.error }}</template>
        </div>
      </form>
    </section>

    <section class="rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">Extraction Prompt</h2>
      <p class="mb-4 text-sm text-muted">
        Field-specific instructions live on each field in the Schema tab; these apply to the whole project.
      </p>
      <div class="flex flex-col gap-4">
        <div class="flex flex-col gap-1.5">
          <label class="text-sm font-medium text-text">System instruction</label>
          <textarea
            v-model="form.systemInstruction"
            rows="4"
            dir="auto"
            :disabled="isArchived"
            placeholder="e.g. You are an assistant that extracts structured data from Persian and English incident reports."
            class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
          />
        </div>
        <div class="flex flex-col gap-1.5">
          <label class="text-sm font-medium text-text">Extraction instruction</label>
          <textarea
            v-model="form.extractionInstruction"
            rows="4"
            dir="auto"
            :disabled="isArchived"
            placeholder="e.g. Dates must be ISO 8601. If a value is not present in the text, return null."
            class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
          />
        </div>
        <p class="text-xs text-muted">Saved together with the provider settings above.</p>
      </div>
    </section>
  </div>
</template>
