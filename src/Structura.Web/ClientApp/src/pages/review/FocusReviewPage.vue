<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../api/client'
import { useToastStore } from '../../stores/toast'
import type { FieldSpec } from '../../api/types'
import BaseButton from '../../components/ui/BaseButton.vue'
import BaseDialog from '../../components/ui/BaseDialog.vue'
import BaseBadge from '../../components/ui/BaseBadge.vue'
import SkeletonRows from '../../components/ui/SkeletonRows.vue'
import DynamicForm from '../../components/DynamicForm.vue'

interface OpenedRecord {
  id: string
  externalId: string
  text: string
  reviewStatus: string
  version: number
  reviewNote: string | null
  fields: FieldSpec[]
  aiOutput: string | null
  workingOutput: string | null
}

const route = useRoute()
const router = useRouter()
const toast = useToastStore()
const queryClient = useQueryClient()

const projectId = computed(() => route.params.projectId as string)
const recordId = computed(() => route.params.recordId as string)

const record = ref<OpenedRecord | null>(null)
const model = ref<Record<string, unknown>>({})
const version = ref(0)
const remaining = ref<number | null>(null)
const loading = ref(true)
const loadError = ref('')
const validationError = ref('')
const busy = ref(false)
const dirty = ref(false)
const textCollapsed = ref(false)

// Reject / reprocess note dialog
const noteDialog = ref<'reject' | 'reprocess' | null>(null)
const note = ref('')

const readOnly = computed(() =>
  record.value !== null
  && record.value.reviewStatus !== 'Assigned'
  && record.value.reviewStatus !== 'InReview')

async function load() {
  loading.value = true
  loadError.value = ''
  validationError.value = ''
  dirty.value = false
  try {
    const opened = await api<OpenedRecord>(`/api/review/${projectId.value}/records/${recordId.value}`)
    record.value = opened
    version.value = opened.version
    model.value = opened.workingOutput ? JSON.parse(opened.workingOutput) : {}
    const next = await api<{ remaining: number }>(`/api/review/${projectId.value}/next`)
    remaining.value = next.remaining
  } catch (error) {
    loadError.value =
      error instanceof ApiError && error.status === 403
        ? 'This record is not assigned to you.'
        : 'Could not load the record.'
  } finally {
    loading.value = false
  }
}

watch(recordId, load, { immediate: true })
watch(model, () => (dirty.value = true), { deep: true })

function resetToAi() {
  if (record.value?.aiOutput) {
    model.value = JSON.parse(record.value.aiOutput)
    toast.info('Restored the AI values.')
  }
}

function handleConflict(error: unknown): boolean {
  if (error instanceof ApiError && error.code === 'version_conflict') {
    toast.error('This record changed elsewhere — reloading it.')
    load()
    return true
  }
  return false
}

async function save() {
  busy.value = true
  try {
    const result = await api<{ version: number }>(
      `/api/review/${projectId.value}/records/${recordId.value}`,
      { method: 'PUT', body: { finalOutput: model.value, version: version.value } },
    )
    version.value = result.version
    dirty.value = false
    toast.success('Saved.')
  } catch (error) {
    if (!handleConflict(error)) toast.error('Could not save your edits.')
  } finally {
    busy.value = false
  }
}

async function decide(action: 'approve' | 'reject' | 'reprocess') {
  busy.value = true
  validationError.value = ''
  try {
    const body =
      action === 'approve'
        ? { finalOutput: model.value, version: version.value }
        : { note: note.value.trim(), version: version.value }
    const result = await api<{ nextRecordId: string | null; remaining: number }>(
      `/api/review/${projectId.value}/records/${recordId.value}/${action}`,
      { method: 'POST', body },
    )
    noteDialog.value = null
    note.value = ''
    toast.success(
      action === 'approve' ? 'Approved ✓' : action === 'reject' ? 'Rejected.' : 'Sent back for reprocessing.')
    queryClient.invalidateQueries({ queryKey: ['review'] })

    // Auto-advance to the next assigned record.
    if (result.nextRecordId) {
      router.replace({ name: 'review-focus', params: { projectId: projectId.value, recordId: result.nextRecordId } })
    } else {
      toast.info('Queue complete 🎉')
      router.push({ name: 'review-home' })
    }
  } catch (error) {
    if (handleConflict(error)) return
    if (error instanceof ApiError && error.status === 422) {
      validationError.value = error.detail ?? 'The values are not valid.'
    } else if (error instanceof ApiError && error.detail) {
      toast.error(error.detail)
    } else {
      toast.error('The action failed.')
    }
  } finally {
    busy.value = false
  }
}

async function goNext() {
  const next = await api<{ recordId: string | null }>(
    `/api/review/${projectId.value}/next?after=${recordId.value}`)
  if (next.recordId && next.recordId !== recordId.value) {
    router.replace({ name: 'review-focus', params: { projectId: projectId.value, recordId: next.recordId } })
  } else {
    toast.info('No other records waiting.')
  }
}

function onKeydown(event: KeyboardEvent) {
  if (!event.altKey || readOnly.value || busy.value) return
  if (event.key.toLowerCase() === 'a') {
    event.preventDefault()
    decide('approve')
  } else if (event.key === 'ArrowRight') {
    event.preventDefault()
    goNext()
  }
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div class="mx-auto flex max-w-3xl flex-col gap-4 pb-24">
    <div class="flex flex-wrap items-center gap-3">
      <RouterLink
        :to="{ name: 'review-list', params: { projectId } }"
        class="text-sm text-muted hover:text-text"
      >
        ← Queue
      </RouterLink>
      <template v-if="record">
        <span class="font-mono text-sm text-text">{{ record.externalId }}</span>
        <BaseBadge :variant="record.reviewStatus === 'Approved' ? 'success' : record.reviewStatus === 'Rejected' ? 'danger' : 'primary'">
          {{ record.reviewStatus }}
        </BaseBadge>
      </template>
      <span v-if="remaining !== null" class="ml-auto text-xs text-muted">
        {{ remaining }} record{{ remaining === 1 ? '' : 's' }} in queue
      </span>
    </div>

    <SkeletonRows v-if="loading" :rows="8" />

    <div
      v-else-if="loadError"
      class="rounded-md border border-danger/40 bg-danger-soft px-3 py-3 text-sm text-danger"
    >
      {{ loadError }}
    </div>

    <template v-else-if="record">
      <!-- Original text -->
      <section class="rounded-xl border border-border bg-surface">
        <button
          class="flex w-full items-center justify-between px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-muted"
          @click="textCollapsed = !textCollapsed"
        >
          Original text <span>{{ textCollapsed ? '▸' : '▾' }}</span>
        </button>
        <p
          v-if="!textCollapsed"
          class="whitespace-pre-wrap border-t border-border px-4 py-3 text-[15px] leading-relaxed text-text"
          dir="auto"
        >
          {{ record.text }}
        </p>
      </section>

      <div
        v-if="record.reviewNote && readOnly"
        class="rounded-md border border-warning/40 bg-warning-soft px-3 py-2 text-sm text-warning"
      >
        Note: {{ record.reviewNote }}
      </div>

      <!-- Extracted data form -->
      <section class="rounded-xl border border-border bg-surface p-4">
        <div class="mb-3 flex items-center justify-between">
          <h2 class="text-xs font-semibold uppercase tracking-wide text-muted">Extracted data</h2>
          <BaseButton
            v-if="!readOnly && record.aiOutput"
            variant="ghost"
            class="!px-2 !py-1 text-xs"
            @click="resetToAi"
          >
            ↩ Reset to AI values
          </BaseButton>
        </div>
        <DynamicForm v-model="model" :fields="record.fields" :disabled="readOnly || busy" />
      </section>

      <div
        v-if="validationError"
        class="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger"
        role="alert"
      >
        {{ validationError }}
      </div>

      <!-- Sticky action bar -->
      <div
        v-if="!readOnly"
        class="fixed inset-x-0 bottom-0 border-t border-border bg-surface/95 px-4 py-3 backdrop-blur"
      >
        <div class="mx-auto flex max-w-3xl items-center gap-2">
          <BaseButton variant="secondary" :disabled="busy || !dirty" @click="save">Save</BaseButton>
          <BaseButton variant="ghost" class="text-warning" :disabled="busy" @click="noteDialog = 'reprocess'">
            ↺ Reprocess
          </BaseButton>
          <BaseButton variant="ghost" class="text-danger" :disabled="busy" @click="noteDialog = 'reject'">
            ✕ Reject
          </BaseButton>
          <div class="ml-auto flex items-center gap-2">
            <BaseButton variant="secondary" :disabled="busy" @click="goNext">Next →</BaseButton>
            <BaseButton :loading="busy" @click="decide('approve')">✓ Approve</BaseButton>
          </div>
        </div>
      </div>
    </template>

    <!-- Reject / reprocess note dialog -->
    <BaseDialog
      :model-value="noteDialog !== null"
      :title="noteDialog === 'reject' ? 'Reject this record?' : 'Send back for reprocessing?'"
      @update:model-value="noteDialog = null"
    >
      <div class="flex flex-col gap-2">
        <p class="text-sm text-muted">
          {{ noteDialog === 'reject'
            ? 'The record will be marked as rejected for the project manager.'
            : 'The AI will process this record again, and it will return to your queue.' }}
        </p>
        <textarea
          v-model="note"
          rows="3"
          dir="auto"
          placeholder="Why? (required)"
          class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text"
        />
      </div>
      <template #footer>
        <BaseButton variant="secondary" @click="noteDialog = null">Cancel</BaseButton>
        <BaseButton
          :variant="noteDialog === 'reject' ? 'danger' : 'primary'"
          :loading="busy"
          :disabled="!note.trim()"
          @click="decide(noteDialog === 'reject' ? 'reject' : 'reprocess')"
        >
          {{ noteDialog === 'reject' ? 'Reject' : 'Send Back' }}
        </BaseButton>
      </template>
    </BaseDialog>
  </div>
</template>
