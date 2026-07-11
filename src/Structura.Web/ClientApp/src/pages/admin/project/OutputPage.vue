<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { subscribeToProject, realtimeConnected } from '../../../api/signalr'
import { useAuthStore } from '../../../stores/auth'
import { useToastStore } from '../../../stores/toast'
import { STATUS_BADGE } from '../../../api/types'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseSelect from '../../../components/ui/BaseSelect.vue'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const auth = useAuthStore()
const toast = useToastStore()
const queryClient = useQueryClient()
const activeTab = ref<'excel' | 'api'>('excel')

// ---------- Excel export ----------
const exporting = ref(false)
async function downloadExcel(format: 'xlsx' | 'csv') {
  exporting.value = true
  try {
    const url = `/api/projects/${projectId}/export/excel${format === 'csv' ? '?format=csv' : ''}`
    const response = await fetch(url, {
      headers: auth.accessToken ? { Authorization: `Bearer ${auth.accessToken}` } : {},
    })
    if (!response.ok) {
      toast.error('Export failed.')
      return
    }
    const blob = await response.blob()
    const disposition = response.headers.get('Content-Disposition') ?? ''
    const match = disposition.match(/filename="(.+?)"/)
    const fileName = match?.[1] ?? `export.${format}`
    const objectUrl = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = objectUrl
    a.download = fileName
    a.click()
    URL.revokeObjectURL(objectUrl)
    toast.success('Export downloaded.')
  } catch {
    toast.error('Export failed.')
  } finally {
    exporting.value = false
  }
}

// ---------- API output config ----------
interface ApiOutputSettings {
  configured: boolean
  url: string
  method: string
  authType: string
  hasToken: boolean
  apiKeyHeaderName: string
  bodyTemplate: string
  successStatusCodes: number[]
  responseIdPath: string
  enabled: boolean
}

const form = ref({
  url: '', method: 'POST', authType: 'none', token: '',
  apiKeyHeaderName: 'X-Api-Key', bodyTemplate: '', successStatusCodes: '200, 201, 202, 204',
  responseIdPath: '', enabled: true,
})
const hasToken = ref(false)
const fieldErrors = ref<Record<string, string>>({})

const configQuery = useQuery({
  queryKey: ['project', projectId, 'api-output'],
  queryFn: () => api<ApiOutputSettings>(`/api/projects/${projectId}/api-output`),
})
watch(
  () => configQuery.data.value,
  (data) => {
    if (!data) return
    form.value = {
      url: data.url, method: data.method, authType: data.authType, token: '',
      apiKeyHeaderName: data.apiKeyHeaderName, bodyTemplate: data.bodyTemplate,
      successStatusCodes: data.successStatusCodes.join(', '),
      responseIdPath: data.responseIdPath, enabled: data.enabled,
    }
    hasToken.value = data.hasToken
  },
  { immediate: true },
)

const saveMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}/api-output`, {
      method: 'PUT',
      body: {
        url: form.value.url,
        method: form.value.method,
        headers: {},
        authType: form.value.authType,
        token: form.value.token || null,
        apiKeyHeaderName: form.value.apiKeyHeaderName,
        bodyTemplate: form.value.bodyTemplate,
        successStatusCodes: form.value.successStatusCodes.split(',').map((s) => parseInt(s.trim(), 10)).filter((n) => !Number.isNaN(n)),
        responseIdPath: form.value.responseIdPath || null,
        enabled: form.value.enabled,
      },
    }),
  onSuccess: () => {
    toast.success('API output saved.')
    fieldErrors.value = {}
    form.value.token = ''
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'api-output'] })
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'deliveries'] })
  },
  onError: (error) => {
    fieldErrors.value = {}
    if (error instanceof ApiError && error.errors) {
      for (const [field, messages] of Object.entries(error.errors)) fieldErrors.value[field] = messages[0]
    } else if (error instanceof ApiError && error.code === 'unsafe_url') {
      fieldErrors.value.url = error.detail ?? 'This URL is not allowed.'
    } else if (error instanceof ApiError && error.detail) {
      toast.error(error.detail)
    } else {
      toast.error('Could not save the connector.')
    }
  },
})

interface TestResult {
  ok: boolean
  rendered: string | null
  sent: boolean
  statusCode: number | null
  response: string | null
  error: string | null
}
const testResult = ref<TestResult | null>(null)
const testMutation = useMutation({
  mutationFn: (send: boolean) =>
    api<TestResult>(`/api/projects/${projectId}/api-output/test${send ? '?send=true' : ''}`, { method: 'POST' }),
  onSuccess: (result) => (testResult.value = result),
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Test failed.') : 'Test failed.'),
})

// ---------- Deliveries ----------
interface DeliveryList {
  counts: { status: string; count: number }[]
  items: { id: string; externalId: string; deliveryStatus: string; attempts: number; deliveredAt: string | null; error: string | null; externalDeliveryId: string | null }[]
}
const deliveriesQuery = useQuery({
  queryKey: ['project', projectId, 'deliveries'],
  queryFn: () => api<DeliveryList>(`/api/projects/${projectId}/deliveries`),
  refetchInterval: (q) => {
    const pending = (q.state.data?.counts ?? []).find((c) => c.status === 'Pending')?.count ?? 0
    return pending > 0 && !realtimeConnected.value ? 3000 : false
  },
})
function deliveryCount(status: string): number {
  return deliveriesQuery.data.value?.counts.find((c) => c.status === status)?.count ?? 0
}

let cleanup: (() => void) | null = null
subscribeToProject(projectId, {
  DeliveryProgress: () => queryClient.invalidateQueries({ queryKey: ['project', projectId, 'deliveries'] }),
}).then((fn) => (cleanup = fn))
onUnmounted(() => cleanup?.())

const startMutation = useMutation({
  mutationFn: () => api<{ queued: number }>(`/api/projects/${projectId}/deliveries/start`, { method: 'POST' }),
  onSuccess: (r) => {
    toast.success(`Queued ${r.queued} record(s) for delivery.`)
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'deliveries'] })
  },
  onError: (error) => toast.error(error instanceof ApiError ? (error.detail ?? 'Failed.') : 'Failed.'),
})
const retryMutation = useMutation({
  mutationFn: () => api<{ requeued: number }>(`/api/projects/${projectId}/deliveries/retry-failed`, { method: 'POST' }),
  onSuccess: (r) => {
    toast.success(`Re-queued ${r.requeued} failed delivery(ies).`)
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'deliveries'] })
  },
  onError: () => toast.error('Retry failed.'),
})

const methodOptions = [{ value: 'POST', label: 'POST' }, { value: 'PUT', label: 'PUT' }, { value: 'PATCH', label: 'PATCH' }]
const authOptions = [
  { value: 'none', label: 'None' },
  { value: 'bearer', label: 'Bearer token' },
  { value: 'apiKey', label: 'API key header' },
]
const approvedTotal = computed(() =>
  (deliveriesQuery.data.value?.counts ?? []).reduce((sum, c) => sum + c.count, 0))
</script>

<template>
  <div class="flex flex-col gap-5">
    <div class="flex gap-1 rounded-lg bg-bg p-1 sm:w-fit">
      <button
        v-for="tab in ([['excel', 'Excel Export'], ['api', 'API Delivery']] as const)"
        :key="tab[0]"
        class="rounded-md px-4 py-1.5 text-sm font-medium"
        :class="activeTab === tab[0] ? 'bg-surface text-text shadow-sm' : 'text-muted hover:text-text'"
        @click="activeTab = tab[0]"
      >
        {{ tab[1] }}
      </button>
    </div>

    <!-- ============ Excel ============ -->
    <section v-if="activeTab === 'excel'" class="max-w-xl rounded-xl border border-border bg-surface p-5">
      <h2 class="mb-1 text-sm font-semibold text-text">Export approved records</h2>
      <p class="mb-4 text-sm text-muted">
        Downloads every <strong>approved</strong> record with its schema fields plus review metadata.
        Cells are sanitized against formula injection.
      </p>
      <div class="flex gap-2">
        <BaseButton :loading="exporting" @click="downloadExcel('xlsx')">⬇ Download Excel (.xlsx)</BaseButton>
        <BaseButton variant="secondary" :loading="exporting" @click="downloadExcel('csv')">Download CSV</BaseButton>
      </div>
    </section>

    <!-- ============ API ============ -->
    <template v-else>
      <SkeletonRows v-if="configQuery.isPending.value" :rows="4" />
      <template v-else>
        <section class="max-w-2xl rounded-xl border border-border bg-surface p-5">
          <div class="mb-1 flex items-center justify-between">
            <h2 class="text-sm font-semibold text-text">API output connector</h2>
            <BaseBadge v-if="configQuery.data.value?.configured" :variant="form.enabled ? 'success' : 'neutral'">
              {{ form.enabled ? 'Enabled' : 'Disabled' }}
            </BaseBadge>
          </div>
          <p class="mb-4 text-sm text-muted">
            Approved records are delivered automatically to this endpoint once enabled.
          </p>
          <form class="flex flex-col gap-4" @submit.prevent="saveMutation.mutate()">
            <div class="grid gap-4 sm:grid-cols-[1fr_110px]">
              <BaseInput v-model="form.url" label="URL" required placeholder="https://api.example.com/ingest"
                :error="fieldErrors.url" :disabled="isArchived" />
              <BaseSelect v-model="form.method" label="Method" :options="methodOptions" :disabled="isArchived" />
            </div>
            <div class="grid gap-4 sm:grid-cols-3">
              <BaseSelect v-model="form.authType" label="Authentication" :options="authOptions" :disabled="isArchived" />
              <BaseInput v-if="form.authType !== 'none'" v-model="form.token"
                :label="hasToken ? 'Token (saved — fill to replace)' : 'Token'" type="password"
                autocomplete="off" :disabled="isArchived" />
              <BaseInput v-if="form.authType === 'apiKey'" v-model="form.apiKeyHeaderName"
                label="Header name" :disabled="isArchived" />
            </div>
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium text-text">Body template (JSON)</label>
              <textarea
                v-model="form.bodyTemplate"
                rows="5"
                dir="ltr"
                :disabled="isArchived"
                placeholder='{"externalId":"{{record.externalId}}","data":{{{output.someField}}},"approved":{{{review.isApproved}}}}'
                class="w-full rounded-md border border-border bg-surface px-3 py-2 font-mono text-xs text-text disabled:opacity-60"
              />
              <p class="text-xs text-muted">
                <span>Placeholders: </span>
                <code v-pre>{{record.externalId}}</code>, <code v-pre>{{output.&lt;field&gt;}}</code>,
                <code v-pre>{{review.reviewer}}</code>, <code v-pre>{{review.approvedAt}}</code>.
                <span>Use triple braces </span><code v-pre>{{{...}}}</code>
                <span> for raw JSON (numbers/booleans/objects). Empty = default body.</span>
              </p>
            </div>
            <div class="grid gap-4 sm:grid-cols-2">
              <BaseInput v-model="form.successStatusCodes" label="Success status codes" :disabled="isArchived" placeholder="200, 201" />
              <BaseInput v-model="form.responseIdPath" label="Response ID path (optional)" :disabled="isArchived" placeholder="id or data.id" />
            </div>
            <label class="flex w-fit cursor-pointer items-center gap-2 text-sm text-text">
              <input v-model="form.enabled" type="checkbox" class="h-4 w-4 accent-[var(--c-primary)]" :disabled="isArchived" />
              Enabled (deliver approved records automatically)
            </label>
            <div class="flex flex-wrap items-center gap-2">
              <BaseButton type="submit" :loading="saveMutation.isPending.value" :disabled="isArchived">Save</BaseButton>
              <BaseButton variant="secondary" :loading="testMutation.isPending.value"
                :disabled="!configQuery.data.value?.configured" @click="testMutation.mutate(false)">
                Preview body
              </BaseButton>
              <BaseButton variant="secondary" :loading="testMutation.isPending.value"
                :disabled="!configQuery.data.value?.configured" @click="testMutation.mutate(true)">
                Send test request
              </BaseButton>
            </div>
          </form>

          <div v-if="testResult" class="mt-4 flex flex-col gap-2">
            <pre class="max-h-40 overflow-auto rounded-md bg-bg p-3 text-xs text-text" dir="ltr">{{ testResult.rendered }}</pre>
            <div
              v-if="testResult.sent"
              class="rounded-md border px-3 py-2 text-sm"
              :class="testResult.ok ? 'border-success/40 bg-success-soft text-success' : 'border-danger/40 bg-danger-soft text-danger'"
            >
              <template v-if="testResult.ok">✓ Sent — HTTP {{ testResult.statusCode }}</template>
              <template v-else>✕ {{ testResult.error ?? `HTTP ${testResult.statusCode}` }}</template>
            </div>
          </div>
        </section>

        <!-- Deliveries -->
        <section class="rounded-xl border border-border bg-surface p-5">
          <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
            <h2 class="text-sm font-semibold text-text">Deliveries</h2>
            <div class="flex items-center gap-3 text-xs">
              <span class="text-muted">Pending {{ deliveryCount('Pending') }}</span>
              <span class="text-success">Delivered {{ deliveryCount('Delivered') }}</span>
              <span class="text-danger">Failed {{ deliveryCount('Failed') }}</span>
            </div>
          </div>
          <div class="mb-4 flex gap-2">
            <BaseButton variant="secondary" class="!py-1"
              :disabled="isArchived || !configQuery.data.value?.configured"
              :loading="startMutation.isPending.value" @click="startMutation.mutate()">
              Queue all approved
            </BaseButton>
            <BaseButton v-if="deliveryCount('Failed') > 0" variant="secondary" class="!py-1"
              :disabled="isArchived" :loading="retryMutation.isPending.value" @click="retryMutation.mutate()">
              ↻ Retry {{ deliveryCount('Failed') }} failed
            </BaseButton>
          </div>

          <SkeletonRows v-if="deliveriesQuery.isPending.value" :rows="3" />
          <EmptyState v-else-if="approvedTotal === 0"
            title="No approved records yet"
            description="Approved records will appear here for delivery.">
            <template #icon>📤</template>
          </EmptyState>
          <div v-else class="overflow-x-auto">
            <table class="w-full min-w-[560px] text-left text-sm">
              <thead>
                <tr class="border-b border-border text-xs uppercase tracking-wide text-muted">
                  <th class="px-3 py-2 font-medium">Record</th>
                  <th class="px-3 py-2 font-medium">Status</th>
                  <th class="px-3 py-2 font-medium">Attempts</th>
                  <th class="px-3 py-2 font-medium">External ID / Error</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-border">
                <tr v-for="item in deliveriesQuery.data.value!.items" :key="item.id">
                  <td class="px-3 py-2 font-mono text-xs text-text">{{ item.externalId }}</td>
                  <td class="px-3 py-2"><BaseBadge :variant="STATUS_BADGE[item.deliveryStatus] ?? 'neutral'">{{ item.deliveryStatus }}</BaseBadge></td>
                  <td class="px-3 py-2 text-muted">{{ item.attempts }}</td>
                  <td class="px-3 py-2 text-xs" dir="ltr">
                    <span v-if="item.externalDeliveryId" class="font-mono text-success">{{ item.externalDeliveryId }}</span>
                    <span v-else-if="item.error" class="text-danger">{{ item.error }}</span>
                    <span v-else class="text-muted">—</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </template>
    </template>
  </div>
</template>
