<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, apiUpload, ApiError } from '../../../api/client'
import { subscribeToProject, realtimeConnected } from '../../../api/signalr'
import { useToastStore } from '../../../stores/toast'
import { STATUS_BADGE, type ImportRunSummary } from '../../../api/types'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseSelect from '../../../components/ui/BaseSelect.vue'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import BaseDialog from '../../../components/ui/BaseDialog.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()
const activeTab = ref<'file' | 'manual' | 'api'>('file')

// ---------- Runs history + live progress ----------
const hasActiveRun = ref(false)
const runsQuery = useQuery({
  queryKey: ['project', projectId, 'imports'],
  queryFn: () => api<{ items: ImportRunSummary[] }>(`/api/projects/${projectId}/imports`),
  // Polling fallback while a run is active and the SignalR socket is down.
  // Reads fresh data straight from the query to avoid racing the watcher.
  refetchInterval: (query) => {
    const active = (query.state.data?.items ?? []).some((r) => r.status === 'Running')
    return active && !realtimeConnected.value ? 3000 : false
  },
})
watch(
  () => runsQuery.data.value,
  (data) => (hasActiveRun.value = (data?.items ?? []).some((r) => r.status === 'Running')),
  { immediate: true },
)

let cleanup: (() => void) | null = null
subscribeToProject(projectId, {
  ImportProgress: () => queryClient.invalidateQueries({ queryKey: ['project', projectId, 'imports'] }),
}).then((fn) => (cleanup = fn))
onUnmounted(() => cleanup?.())

// ---------- File upload + mapping ----------
interface UploadResult {
  id: string
  fileName: string
  columns: string[]
  previewRows: Record<string, string | null>[]
}
const upload = ref<UploadResult | null>(null)
const idColumn = ref('')
const textColumn = ref('')
const generateIds = ref(false)
const uploadError = ref('')

const uploadMutation = useMutation({
  mutationFn: (file: File) => apiUpload<UploadResult>(`/api/projects/${projectId}/imports/upload`, file),
  onSuccess: (result) => {
    upload.value = result
    uploadError.value = ''
    idColumn.value = result.columns[0] ?? ''
    textColumn.value = result.columns.find((c) => /text|body|report/i.test(c)) ?? result.columns[1] ?? result.columns[0] ?? ''
    generateIds.value = false
  },
  onError: (error) => {
    upload.value = null
    uploadError.value = error instanceof ApiError ? (error.detail ?? 'Upload failed.') : 'Upload failed.'
  },
})

function onFileChosen(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) uploadMutation.mutate(file)
  input.value = ''
}

const startMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}/imports/${upload.value!.id}/start`, {
      method: 'POST',
      body: {
        idColumn: generateIds.value ? null : idColumn.value,
        textColumn: textColumn.value,
        generateIds: generateIds.value,
      },
    }),
  onSuccess: () => {
    toast.success('Import started.')
    upload.value = null
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'imports'] })
  },
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Could not start the import.') : 'Could not start the import.'),
})

const cancelMutation = useMutation({
  mutationFn: (runId: string) => api(`/api/projects/${projectId}/imports/${runId}/cancel`, { method: 'POST' }),
  onSuccess: () => {
    toast.info('Import cancelled.')
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'imports'] })
  },
  onError: () => toast.error('Could not cancel the import.'),
})

// ---------- Row errors dialog ----------
const errorsFor = ref<string | null>(null)
const errorsQuery = useQuery({
  queryKey: computed(() => ['project', projectId, 'imports', errorsFor.value, 'errors']),
  queryFn: () => api<{ errors: { row: number; message: string }[] }>(
    `/api/projects/${projectId}/imports/${errorsFor.value}`),
  enabled: computed(() => errorsFor.value !== null),
})

// ---------- Manual input ----------
const manualId = ref('')
const manualText = ref('')
const bulkText = ref('')
const bulkParsed = computed(() => {
  const lines = bulkText.value.split('\n').map((l) => l.trim()).filter(Boolean)
  return lines.map((line) => {
    const tab = line.indexOf('\t')
    return tab > 0
      ? { externalId: line.slice(0, tab).trim(), text: line.slice(tab + 1).trim() }
      : { externalId: null as string | null, text: line }
  }).filter((r) => r.text)
})

const manualMutation = useMutation({
  mutationFn: (records: { externalId: string | null; text: string }[]) =>
    api<{ imported: number; skippedDuplicates: number }>(`/api/projects/${projectId}/records/manual`, {
      method: 'POST',
      body: { records },
    }),
  onSuccess: (result) => {
    toast.success(`Imported ${result.imported} record(s)` +
      (result.skippedDuplicates ? `, skipped ${result.skippedDuplicates} duplicate(s).` : '.'))
    manualId.value = ''
    manualText.value = ''
    bulkText.value = ''
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'imports'] })
  },
  onError: () => toast.error('Could not add the records.'),
})

// ---------- API input ----------
interface ApiInputSettings {
  configured: boolean
  url: string
  method: string
  authType: string
  hasToken: boolean
  apiKeyHeaderName: string
  dataPath: string
  idPath: string
  textPath: string
}
interface ApiFetchResult {
  ok: boolean
  statusCode: number
  error: string | null
  totalItems?: number
  mappingErrors?: number
  alreadyExisting?: number
  imported?: number
  skippedDuplicates?: number
  items?: { externalId: string; textExcerpt: string; duplicate: boolean }[]
}

const apiForm = ref({
  url: '', method: 'GET', authType: 'none', token: '',
  apiKeyHeaderName: 'X-Api-Key', dataPath: '', idPath: '', textPath: '',
})
const apiHasToken = ref(false)
const apiResult = ref<ApiFetchResult | null>(null)
const apiFieldErrors = ref<Record<string, string>>({})

const apiConfigQuery = useQuery({
  queryKey: ['project', projectId, 'api-input'],
  queryFn: () => api<ApiInputSettings>(`/api/projects/${projectId}/api-input`),
})
watch(
  () => apiConfigQuery.data.value,
  (data) => {
    if (!data) return
    apiForm.value = {
      url: data.url, method: data.method, authType: data.authType, token: '',
      apiKeyHeaderName: data.apiKeyHeaderName, dataPath: data.dataPath,
      idPath: data.idPath, textPath: data.textPath,
    }
    apiHasToken.value = data.hasToken
  },
  { immediate: true },
)

const apiSaveMutation = useMutation({
  mutationFn: () =>
    api(`/api/projects/${projectId}/api-input`, {
      method: 'PUT',
      body: { ...apiForm.value, token: apiForm.value.token || null, idPath: apiForm.value.idPath || null },
    }),
  onSuccess: () => {
    toast.success('API input saved.')
    apiFieldErrors.value = {}
    apiForm.value.token = ''
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'api-input'] })
  },
  onError: (error) => {
    apiFieldErrors.value = {}
    if (error instanceof ApiError && error.errors) {
      for (const [field, messages] of Object.entries(error.errors)) apiFieldErrors.value[field] = messages[0]
    } else if (error instanceof ApiError && error.code === 'unsafe_url') {
      apiFieldErrors.value.url = error.detail ?? 'This URL is not allowed.'
    } else if (error instanceof ApiError && error.detail) {
      toast.error(error.detail)
    } else {
      toast.error('Could not save the API input.')
    }
  },
})

const apiRunMutation = useMutation({
  mutationFn: (mode: 'test' | 'fetch') =>
    api<ApiFetchResult>(`/api/projects/${projectId}/api-input/${mode}`, { method: 'POST' }),
  onSuccess: (result, mode) => {
    apiResult.value = result
    if (mode === 'fetch' && result.ok) {
      toast.success(`Fetched ${result.imported} new record(s).`)
      queryClient.invalidateQueries({ queryKey: ['project', projectId, 'imports'] })
    }
  },
  onError: (error) =>
    toast.error(error instanceof ApiError ? (error.detail ?? 'Request failed.') : 'Request failed.'),
})

const methodOptions = [{ value: 'GET', label: 'GET' }, { value: 'POST', label: 'POST' }]
const authOptions = [
  { value: 'none', label: 'None' },
  { value: 'bearer', label: 'Bearer token' },
  { value: 'apiKey', label: 'API key header' },
]

function progressPercent(run: ImportRunSummary): number | null {
  if (!run.totalRows || run.totalRows === 0) return null
  const done = run.imported + run.skippedDuplicates + run.failed
  return Math.min(100, Math.round((done / run.totalRows) * 100))
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <div class="flex gap-1 rounded-lg bg-bg p-1 sm:w-fit">
      <button
        v-for="tab in ([['file', 'Excel / CSV'], ['manual', 'Manual'], ['api', 'API']] as const)"
        :key="tab[0]"
        class="rounded-md px-4 py-1.5 text-sm font-medium"
        :class="activeTab === tab[0] ? 'bg-surface text-text shadow-sm' : 'text-muted hover:text-text'"
        @click="activeTab = tab[0]"
      >
        {{ tab[1] }}
      </button>
    </div>

    <!-- ============ Excel / CSV tab ============ -->
    <template v-if="activeTab === 'file'">
      <section v-if="!isArchived" class="rounded-xl border border-border bg-surface p-5">
        <template v-if="!upload">
          <label
            class="flex cursor-pointer flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed border-border px-6 py-10 text-center hover:border-primary/60"
          >
            <span class="text-3xl">📄</span>
            <span class="text-sm font-medium text-text">
              {{ uploadMutation.isPending.value ? 'Uploading…' : 'Choose an .xlsx or .csv file' }}
            </span>
            <span class="text-xs text-muted">Up to 50 MB · 100,000 rows · needs an ID column and a Text column</span>
            <input type="file" accept=".xlsx,.csv" class="hidden" :disabled="uploadMutation.isPending.value" @change="onFileChosen" />
          </label>
          <p v-if="uploadError" class="mt-3 rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger">
            {{ uploadError }}
          </p>
        </template>

        <template v-else>
          <div class="mb-4 flex items-center justify-between">
            <h2 class="text-sm font-semibold text-text">Map columns — {{ upload.fileName }}</h2>
            <BaseButton variant="ghost" @click="upload = null">✕ Discard</BaseButton>
          </div>
          <div class="mb-4 grid gap-4 sm:grid-cols-3">
            <div>
              <BaseSelect
                v-model="idColumn"
                label="Record ID column"
                :options="upload.columns.map((c) => ({ value: c, label: c }))"
                :disabled="generateIds"
              />
              <label class="mt-2 flex cursor-pointer items-center gap-2 text-xs text-muted">
                <input v-model="generateIds" type="checkbox" class="h-3.5 w-3.5 accent-[var(--c-primary)]" />
                Generate IDs automatically
              </label>
            </div>
            <BaseSelect
              v-model="textColumn"
              label="Text column"
              required
              :options="upload.columns.map((c) => ({ value: c, label: c }))"
            />
            <div class="flex items-end">
              <BaseButton
                :loading="startMutation.isPending.value"
                :disabled="!textColumn"
                @click="startMutation.mutate()"
              >
                Start Import
              </BaseButton>
            </div>
          </div>
          <div class="overflow-x-auto rounded-lg border border-border">
            <table class="w-full min-w-[560px] text-left text-xs">
              <thead>
                <tr class="border-b border-border bg-bg text-muted">
                  <th v-for="col in upload.columns" :key="col" class="px-3 py-2 font-medium"
                    :class="{ 'text-primary': col === textColumn || (!generateIds && col === idColumn) }">
                    {{ col }}
                    <span v-if="!generateIds && col === idColumn"> (ID)</span>
                    <span v-if="col === textColumn"> (Text)</span>
                  </th>
                </tr>
              </thead>
              <tbody class="divide-y divide-border">
                <tr v-for="(row, i) in upload.previewRows" :key="i">
                  <td v-for="col in upload.columns" :key="col" class="max-w-64 truncate px-3 py-1.5 text-text" dir="auto">
                    {{ row[col] ?? '' }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="mt-2 text-xs text-muted">Preview of the first {{ upload.previewRows.length }} rows.</p>
        </template>
      </section>

      <!-- Import history -->
      <section class="rounded-xl border border-border bg-surface p-5">
        <h2 class="mb-3 text-sm font-semibold text-text">Import history</h2>
        <EmptyState
          v-if="(runsQuery.data.value?.items ?? []).length === 0"
          title="No imports yet"
          description="Upload a file, paste records manually, or fetch from an API."
        >
          <template #icon>📥</template>
        </EmptyState>
        <ul v-else class="divide-y divide-border">
          <li v-for="run in runsQuery.data.value!.items" :key="run.id" class="py-3">
            <div class="flex flex-wrap items-center gap-2">
              <BaseBadge :variant="STATUS_BADGE[run.status] ?? 'neutral'">{{ run.status }}</BaseBadge>
              <span class="text-sm font-medium text-text">{{ run.fileName ?? run.source }}</span>
              <span class="text-xs text-muted">{{ new Date(run.createdAt).toLocaleString() }}</span>
              <span class="ml-auto flex items-center gap-2 text-xs text-muted">
                <span class="text-success">{{ run.imported }} imported</span>
                <span v-if="run.skippedDuplicates">· {{ run.skippedDuplicates }} duplicates</span>
                <button
                  v-if="run.failed > 0"
                  class="text-danger underline-offset-2 hover:underline"
                  @click="errorsFor = run.id"
                >
                  {{ run.failed }} errors
                </button>
                <BaseButton
                  v-if="run.status === 'Running'"
                  variant="ghost"
                  class="!px-2 !py-0.5 text-danger"
                  :loading="cancelMutation.isPending.value"
                  @click="cancelMutation.mutate(run.id)"
                >
                  Cancel
                </BaseButton>
              </span>
            </div>
            <div v-if="run.status === 'Running'" class="mt-2 h-1.5 overflow-hidden rounded-full bg-bg">
              <div
                class="h-full rounded-full bg-primary transition-all"
                :style="{ width: `${progressPercent(run) ?? 30}%` }"
                :class="{ 'animate-pulse': progressPercent(run) === null }"
              />
            </div>
          </li>
        </ul>
      </section>
    </template>

    <!-- ============ Manual tab ============ -->
    <template v-else-if="activeTab === 'manual'">
      <div class="grid gap-5 lg:grid-cols-2">
        <section class="rounded-xl border border-border bg-surface p-5">
          <h2 class="mb-3 text-sm font-semibold text-text">Single record</h2>
          <form
            class="flex flex-col gap-4"
            @submit.prevent="manualMutation.mutate([{ externalId: manualId || null, text: manualText }])"
          >
            <BaseInput v-model="manualId" label="Record ID (optional)" placeholder="Generated if left empty" :disabled="isArchived" />
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium text-text">Text <span class="text-danger">*</span></label>
              <textarea
                v-model="manualText"
                rows="6"
                dir="auto"
                :disabled="isArchived"
                class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
              />
            </div>
            <div>
              <BaseButton type="submit" :loading="manualMutation.isPending.value" :disabled="isArchived || !manualText.trim()">
                Add Record
              </BaseButton>
            </div>
          </form>
        </section>

        <section class="rounded-xl border border-border bg-surface p-5">
          <h2 class="mb-1 text-sm font-semibold text-text">Bulk paste</h2>
          <p class="mb-3 text-xs text-muted">One record per line. Use <code>ID⇥TAB⇥Text</code> to include IDs.</p>
          <textarea
            v-model="bulkText"
            rows="8"
            dir="auto"
            :disabled="isArchived"
            placeholder="C-1001&#9;در تاریخ ۱۲ مرداد ...&#10;C-1002&#9;The customer reported ..."
            class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
          />
          <div class="mt-3 flex items-center gap-3">
            <BaseButton
              :loading="manualMutation.isPending.value"
              :disabled="isArchived || bulkParsed.length === 0"
              @click="manualMutation.mutate(bulkParsed)"
            >
              Add {{ bulkParsed.length }} Record{{ bulkParsed.length === 1 ? '' : 's' }}
            </BaseButton>
            <span v-if="bulkParsed.length > 1000" class="text-xs text-danger">Maximum 1,000 per paste.</span>
          </div>
        </section>
      </div>
    </template>

    <!-- ============ API tab ============ -->
    <template v-else>
      <section class="max-w-2xl rounded-xl border border-border bg-surface p-5">
        <h2 class="mb-1 text-sm font-semibold text-text">API input source</h2>
        <p class="mb-4 text-sm text-muted">Fetch records from an external JSON API. Runs on demand.</p>
        <form class="flex flex-col gap-4" @submit.prevent="apiSaveMutation.mutate()">
          <div class="grid gap-4 sm:grid-cols-[1fr_120px]">
            <BaseInput v-model="apiForm.url" label="URL" required placeholder="https://api.example.com/v2/reports"
              :error="apiFieldErrors.url" :disabled="isArchived" />
            <BaseSelect v-model="apiForm.method" label="Method" :options="methodOptions" :disabled="isArchived" />
          </div>
          <div class="grid gap-4 sm:grid-cols-3">
            <BaseSelect v-model="apiForm.authType" label="Authentication" :options="authOptions" :disabled="isArchived" />
            <BaseInput
              v-if="apiForm.authType !== 'none'"
              v-model="apiForm.token"
              :label="apiHasToken ? 'Token (saved — fill to replace)' : 'Token'"
              type="password"
              autocomplete="off"
              :disabled="isArchived"
            />
            <BaseInput
              v-if="apiForm.authType === 'apiKey'"
              v-model="apiForm.apiKeyHeaderName"
              label="Header name"
              :disabled="isArchived"
            />
          </div>
          <div class="grid gap-4 sm:grid-cols-3">
            <BaseInput v-model="apiForm.dataPath" label="Data array path" placeholder="data.items (empty = root)"
              :error="apiFieldErrors.dataPath" :disabled="isArchived" />
            <BaseInput v-model="apiForm.idPath" label="ID path" placeholder="id (empty = generate)" :disabled="isArchived" />
            <BaseInput v-model="apiForm.textPath" label="Text path" required placeholder="body"
              :error="apiFieldErrors.textPath" :disabled="isArchived" />
          </div>
          <div class="flex flex-wrap items-center gap-2">
            <BaseButton type="submit" :loading="apiSaveMutation.isPending.value" :disabled="isArchived">Save</BaseButton>
            <BaseButton variant="secondary" :loading="apiRunMutation.isPending.value"
              :disabled="!apiConfigQuery.data.value?.configured" @click="apiRunMutation.mutate('test')">
              Test (preview only)
            </BaseButton>
            <BaseButton variant="secondary" :loading="apiRunMutation.isPending.value"
              :disabled="isArchived || !apiConfigQuery.data.value?.configured" @click="apiRunMutation.mutate('fetch')">
              Fetch Now
            </BaseButton>
          </div>
        </form>

        <div v-if="apiResult" class="mt-4">
          <div
            class="rounded-md border px-3 py-2 text-sm"
            :class="apiResult.ok ? 'border-success/40 bg-success-soft text-success' : 'border-danger/40 bg-danger-soft text-danger'"
          >
            <template v-if="apiResult.ok">
              ✓ {{ apiResult.totalItems }} items
              <span v-if="apiResult.imported !== undefined"> · {{ apiResult.imported }} imported · {{ apiResult.skippedDuplicates }} duplicates</span>
              <span v-else> · {{ apiResult.alreadyExisting }} already exist</span>
              <span v-if="apiResult.mappingErrors"> · {{ apiResult.mappingErrors }} mapping errors</span>
            </template>
            <template v-else>✕ {{ apiResult.error }}</template>
          </div>
          <table v-if="apiResult.items?.length" class="mt-3 w-full text-left text-xs">
            <thead>
              <tr class="border-b border-border text-muted">
                <th class="px-2 py-1.5 font-medium">ID</th>
                <th class="px-2 py-1.5 font-medium">Text</th>
                <th class="px-2 py-1.5 font-medium"></th>
              </tr>
            </thead>
            <tbody class="divide-y divide-border">
              <tr v-for="item in apiResult.items" :key="item.externalId">
                <td class="px-2 py-1.5 font-mono text-text">{{ item.externalId }}</td>
                <td class="max-w-96 truncate px-2 py-1.5 text-text" dir="auto">{{ item.textExcerpt }}</td>
                <td class="px-2 py-1.5">
                  <BaseBadge v-if="item.duplicate" variant="warning">duplicate</BaseBadge>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </template>

    <!-- Row errors dialog -->
    <BaseDialog :model-value="errorsFor !== null" title="Row errors" @update:model-value="errorsFor = null">
      <div class="max-h-80 overflow-y-auto">
        <table class="w-full text-left text-sm">
          <thead>
            <tr class="border-b border-border text-xs text-muted">
              <th class="py-1.5 pr-4 font-medium">Row</th>
              <th class="py-1.5 font-medium">Problem</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-border">
            <tr v-for="error in errorsQuery.data.value?.errors ?? []" :key="`${error.row}-${error.message}`">
              <td class="py-1.5 pr-4 text-muted">{{ error.row || '—' }}</td>
              <td class="py-1.5 text-text">{{ error.message }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </BaseDialog>
  </div>
</template>
