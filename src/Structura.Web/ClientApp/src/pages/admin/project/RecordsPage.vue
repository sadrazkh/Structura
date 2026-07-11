<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from '../../../api/client'
import { useToastStore } from '../../../stores/toast'
import { STATUS_BADGE, type RecordListItem } from '../../../api/types'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseSelect from '../../../components/ui/BaseSelect.vue'
import BaseBadge from '../../../components/ui/BaseBadge.vue'
import ConfirmDialog from '../../../components/ui/ConfirmDialog.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

interface RecordDetail {
  id: string
  externalId: string
  text: string
  processingStatus: string
  reviewStatus: string
  deliveryStatus: string
  processingError: string | null
  reviewer: { id: string; fullName: string } | null
  finalOutput: string | null
  reviewNote: string | null
  version: number
  createdAt: string
  updatedAt: string
  extractions: {
    id: string
    model: string
    status: string
    output: string | null
    error: string | null
    inputTokens: number
    outputTokens: number
    durationMs: number
    createdAt: string
  }[]
}

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()

// ---------- Filters ----------
const q = ref('')
const debouncedQ = ref('')
let debounceTimer: ReturnType<typeof setTimeout> | undefined
watch(q, (value) => {
  clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => (debouncedQ.value = value.trim()), 350)
})
const processingStatus = ref('')
const reviewStatus = ref('')

const filterKey = computed(() => ({
  q: debouncedQ.value,
  processingStatus: processingStatus.value,
  reviewStatus: reviewStatus.value,
}))

// ---------- Accumulated keyset pages ----------
const records = ref<RecordListItem[]>([])
const nextCursor = ref<string | null>(null)
const loadingMore = ref(false)

function buildUrl(cursor?: string | null): string {
  const params = new URLSearchParams()
  if (debouncedQ.value) params.set('q', debouncedQ.value)
  if (processingStatus.value) params.set('processingStatus', processingStatus.value)
  if (reviewStatus.value) params.set('reviewStatus', reviewStatus.value)
  if (cursor) params.set('cursor', cursor)
  const query = params.toString()
  return `/api/projects/${projectId}/records${query ? `?${query}` : ''}`
}

const firstPageQuery = useQuery({
  queryKey: computed(() => ['project', projectId, 'records', filterKey.value]),
  queryFn: () => api<{ items: RecordListItem[]; nextCursor: string | null }>(buildUrl()),
})
watch(
  () => firstPageQuery.data.value,
  (data) => {
    if (data) {
      records.value = data.items
      nextCursor.value = data.nextCursor
      selection.value.clear()
    }
  },
  { immediate: true },
)

async function loadMore() {
  if (!nextCursor.value) return
  loadingMore.value = true
  try {
    const page = await api<{ items: RecordListItem[]; nextCursor: string | null }>(buildUrl(nextCursor.value))
    records.value = [...records.value, ...page.items]
    nextCursor.value = page.nextCursor
  } finally {
    loadingMore.value = false
  }
}

// ---------- Counts ----------
const countsQuery = useQuery({
  queryKey: ['project', projectId, 'record-counts'],
  queryFn: () => api<{ processing: { status: string; count: number }[] }>(
    `/api/projects/${projectId}/records/counts`),
})
const totalCount = computed(() =>
  (countsQuery.data.value?.processing ?? []).reduce((sum, g) => sum + g.count, 0))

// ---------- Selection + delete ----------
const selection = ref(new Set<string>())
function toggle(id: string, checked: boolean) {
  const next = new Set(selection.value)
  if (checked) next.add(id)
  else next.delete(id)
  selection.value = next
}
const confirmDelete = ref(false)
const deleteMutation = useMutation({
  mutationFn: () =>
    api<{ deleted: number; skipped: number }>(`/api/projects/${projectId}/records/delete`, {
      method: 'POST',
      body: { recordIds: [...selection.value] },
    }),
  onSuccess: (result) => {
    confirmDelete.value = false
    toast.success(`Deleted ${result.deleted} record(s)` +
      (result.skipped ? `; ${result.skipped} skipped (already processed or assigned).` : '.'))
    selection.value = new Set()
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'records'] })
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'record-counts'] })
  },
  onError: () => toast.error('Could not delete the records.'),
})

// ---------- Detail drawer ----------
const openRecordId = ref<string | null>(null)
const detailQuery = useQuery({
  queryKey: computed(() => ['project', projectId, 'record', openRecordId.value]),
  queryFn: () => api<RecordDetail>(`/api/projects/${projectId}/records/${openRecordId.value}`),
  enabled: computed(() => openRecordId.value !== null),
})

const processingOptions = [
  { value: '', label: 'Processing: all' },
  ...['Pending', 'Processing', 'Completed', 'Failed'].map((s) => ({ value: s, label: s })),
]
const reviewOptions = [
  { value: '', label: 'Review: all' },
  ...['Unassigned', 'Assigned', 'InReview', 'Approved', 'Rejected', 'ReprocessRequested']
    .map((s) => ({ value: s, label: s })),
]
</script>

<template>
  <div class="flex flex-col gap-4">
    <!-- Filter bar -->
    <div class="flex flex-wrap items-end gap-3">
      <div class="w-64">
        <BaseInput v-model="q" placeholder="Search text or ID…" />
      </div>
      <div class="w-44"><BaseSelect v-model="processingStatus" :options="processingOptions" /></div>
      <div class="w-44"><BaseSelect v-model="reviewStatus" :options="reviewOptions" /></div>
      <span class="ml-auto text-sm text-muted">{{ totalCount.toLocaleString() }} records total</span>
    </div>

    <!-- Bulk actions -->
    <div
      v-if="selection.size > 0"
      class="flex items-center gap-3 rounded-lg border border-primary/40 bg-primary-soft px-3 py-2"
    >
      <span class="text-sm font-medium text-primary">{{ selection.size }} selected</span>
      <BaseButton v-if="!isArchived" variant="danger" class="!py-1" @click="confirmDelete = true">
        Delete
      </BaseButton>
      <BaseButton variant="ghost" class="!py-1" @click="selection = new Set()">Clear</BaseButton>
    </div>

    <SkeletonRows v-if="firstPageQuery.isPending.value" :rows="8" />

    <EmptyState
      v-else-if="records.length === 0"
      title="No records found"
      description="Import records from the Import tab, or adjust your filters."
    >
      <template #icon>🗂</template>
      <template #action>
        <BaseButton variant="secondary" @click="$router.push({ name: 'project-import' })">Go to Import</BaseButton>
      </template>
    </EmptyState>

    <template v-else>
      <div class="overflow-x-auto rounded-xl border border-border bg-surface">
        <table class="w-full min-w-[720px] text-left text-sm">
          <thead>
            <tr class="border-b border-border text-xs uppercase tracking-wide text-muted">
              <th class="w-10 px-3 py-2.5"></th>
              <th class="px-3 py-2.5 font-medium">ID</th>
              <th class="px-3 py-2.5 font-medium">Text</th>
              <th class="px-3 py-2.5 font-medium">Processing</th>
              <th class="px-3 py-2.5 font-medium">Review</th>
              <th class="px-3 py-2.5 font-medium">Updated</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-border">
            <tr
              v-for="record in records"
              :key="record.id"
              class="cursor-pointer hover:bg-bg"
              @click="openRecordId = record.id"
            >
              <td class="px-3 py-2.5" @click.stop>
                <input
                  type="checkbox"
                  class="h-4 w-4 accent-[var(--c-primary)]"
                  :checked="selection.has(record.id)"
                  @change="toggle(record.id, ($event.target as HTMLInputElement).checked)"
                />
              </td>
              <td class="whitespace-nowrap px-3 py-2.5 font-mono text-xs text-text">{{ record.externalId }}</td>
              <td class="max-w-md truncate px-3 py-2.5 text-text" dir="auto">{{ record.textExcerpt }}</td>
              <td class="px-3 py-2.5">
                <BaseBadge :variant="STATUS_BADGE[record.processingStatus] ?? 'neutral'">
                  {{ record.processingStatus }}
                </BaseBadge>
              </td>
              <td class="px-3 py-2.5">
                <BaseBadge :variant="STATUS_BADGE[record.reviewStatus] ?? 'neutral'">
                  {{ record.reviewStatus }}
                </BaseBadge>
              </td>
              <td class="whitespace-nowrap px-3 py-2.5 text-xs text-muted">
                {{ new Date(record.updatedAt).toLocaleString() }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="nextCursor" class="flex justify-center">
        <BaseButton variant="secondary" :loading="loadingMore" @click="loadMore">Load more</BaseButton>
      </div>
    </template>

    <!-- Detail drawer -->
    <Teleport to="body">
      <div v-if="openRecordId" class="fixed inset-0 z-50">
        <div class="absolute inset-0 bg-black/40" @click="openRecordId = null" />
        <aside
          class="absolute right-0 top-0 flex h-full w-full max-w-xl flex-col border-l border-border bg-surface shadow-2xl"
        >
          <div class="flex items-center justify-between border-b border-border px-5 py-3">
            <h2 class="text-sm font-semibold text-text">Record details</h2>
            <button class="rounded p-1 text-muted hover:bg-bg hover:text-text" aria-label="Close" @click="openRecordId = null">✕</button>
          </div>
          <div class="flex-1 overflow-y-auto p-5">
            <SkeletonRows v-if="detailQuery.isPending.value" :rows="6" />
            <template v-else-if="detailQuery.data.value">
              <div class="mb-4 flex flex-wrap items-center gap-2">
                <span class="font-mono text-sm text-text">{{ detailQuery.data.value.externalId }}</span>
                <BaseBadge :variant="STATUS_BADGE[detailQuery.data.value.processingStatus] ?? 'neutral'">
                  {{ detailQuery.data.value.processingStatus }}
                </BaseBadge>
                <BaseBadge :variant="STATUS_BADGE[detailQuery.data.value.reviewStatus] ?? 'neutral'">
                  {{ detailQuery.data.value.reviewStatus }}
                </BaseBadge>
                <span v-if="detailQuery.data.value.reviewer" class="text-xs text-muted">
                  Reviewer: {{ detailQuery.data.value.reviewer.fullName }}
                </span>
              </div>

              <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-muted">Original text</h3>
              <p class="mb-4 whitespace-pre-wrap rounded-lg border border-border bg-bg p-3 text-sm text-text" dir="auto">
                {{ detailQuery.data.value.text }}
              </p>

              <div
                v-if="detailQuery.data.value.processingError"
                class="mb-4 rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger"
              >
                {{ detailQuery.data.value.processingError }}
              </div>

              <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-muted">AI extractions</h3>
              <p v-if="detailQuery.data.value.extractions.length === 0" class="text-sm text-muted">
                Not processed yet — send this record to AI from a processing run.
              </p>
              <ul v-else class="flex flex-col gap-2">
                <li
                  v-for="extraction in detailQuery.data.value.extractions"
                  :key="extraction.id"
                  class="rounded-lg border border-border p-3"
                >
                  <div class="mb-1 flex flex-wrap items-center gap-2 text-xs text-muted">
                    <BaseBadge :variant="extraction.status === 'Succeeded' ? 'success' : 'danger'">
                      {{ extraction.status }}
                    </BaseBadge>
                    <span>{{ extraction.model }}</span>
                    <span>· {{ extraction.durationMs }} ms</span>
                    <span>· {{ extraction.inputTokens }}/{{ extraction.outputTokens }} tokens</span>
                    <span class="ml-auto">{{ new Date(extraction.createdAt).toLocaleString() }}</span>
                  </div>
                  <pre
                    v-if="extraction.output"
                    class="overflow-x-auto rounded bg-bg p-2 text-xs text-text"
                    dir="ltr"
                  >{{ JSON.stringify(JSON.parse(extraction.output), null, 2) }}</pre>
                  <p v-if="extraction.error" class="text-xs text-danger">{{ extraction.error }}</p>
                </li>
              </ul>
            </template>
          </div>
        </aside>
      </div>
    </Teleport>

    <ConfirmDialog
      v-model="confirmDelete"
      title="Delete selected records?"
      :message="`Only records that are still Pending and Unassigned will be deleted. ${selection.size} selected.`"
      confirm-label="Delete"
      danger
      :loading="deleteMutation.isPending.value"
      @confirm="deleteMutation.mutate()"
    />
  </div>
</template>
