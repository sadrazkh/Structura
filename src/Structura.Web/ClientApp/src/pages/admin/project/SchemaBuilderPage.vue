<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api, ApiError } from '../../../api/client'
import { useToastStore } from '../../../stores/toast'
import { FIELD_TYPE_LABELS, type FieldSpec, type FieldType } from '../../../api/types'
import BaseButton from '../../../components/ui/BaseButton.vue'
import BaseInput from '../../../components/ui/BaseInput.vue'
import BaseSelect from '../../../components/ui/BaseSelect.vue'
import EmptyState from '../../../components/ui/EmptyState.vue'
import SkeletonRows from '../../../components/ui/SkeletonRows.vue'
import DynamicForm from '../../../components/DynamicForm.vue'
import type { ProjectDetail } from './ProjectLayout.vue'

const props = defineProps<{ project: ProjectDetail }>()
const projectId = props.project.id
const isArchived = props.project.status === 'Archived'

const toast = useToastStore()
const queryClient = useQueryClient()

const fields = ref<FieldSpec[]>([])
const selectedIndex = ref<number | null>(null)
const dirty = ref(false)
const serverErrors = ref<string[]>([])
const previewTab = ref<'form' | 'json'>('form')
const previewModel = ref<Record<string, unknown>>({})

const schemaQuery = useQuery({
  queryKey: ['project', projectId, 'schema'],
  queryFn: () => api<{ version: number; fields: FieldSpec[] }>(`/api/projects/${projectId}/schema`),
})

watch(
  () => schemaQuery.data.value,
  (data) => {
    if (data && !dirty.value) {
      // Query data is a reactive proxy — structuredClone would throw; JSON clone is safe here.
      fields.value = JSON.parse(JSON.stringify(data.fields))
      if (fields.value.length > 0 && selectedIndex.value === null) selectedIndex.value = 0
    }
  },
  { immediate: true },
)

const selected = computed(() =>
  selectedIndex.value !== null ? fields.value[selectedIndex.value] : null,
)

const typeOptions = Object.entries(FIELD_TYPE_LABELS).map(([value, label]) => ({ value, label }))

const jsonShape = computed(() => {
  const shape: Record<string, string> = {}
  for (const field of fields.value) {
    shape[field.key || '?'] = field.type === 'multiSelect' ? 'string[]'
      : field.type === 'integer' || field.type === 'decimal' ? 'number'
      : field.type === 'boolean' ? 'boolean'
      : 'string'
  }
  return JSON.stringify(shape, null, 2)
})

// --- allowedValues editing (textarea, one per line) ---
const allowedValuesText = computed({
  get: () => (selected.value?.allowedValues ?? []).join('\n'),
  set: (text: string) => {
    if (!selected.value) return
    selected.value.allowedValues = text.split('\n').map((v) => v.trim()).filter(Boolean)
    markDirty()
  },
})

function markDirty() {
  dirty.value = true
  serverErrors.value = []
}

function camelCase(label: string): string {
  const words = label.trim().split(/[^\p{L}\p{N}]+/u).filter(Boolean)
  const ascii = words
    .map((w, i) => {
      const lower = w.toLowerCase()
      return i === 0 ? lower : lower[0].toUpperCase() + lower.slice(1)
    })
    .join('')
    .replace(/[^a-zA-Z0-9]/g, '')
  return /^[a-z]/.test(ascii) ? ascii.slice(0, 64) : ''
}

function addField() {
  fields.value.push({
    key: '',
    label: '',
    type: 'shortText',
    required: false,
    description: '',
    extractionInstruction: '',
    allowedValues: null,
    defaultValue: null,
    displayOrder: fields.value.length,
  })
  selectedIndex.value = fields.value.length - 1
  markDirty()
}

function removeField(index: number) {
  fields.value.splice(index, 1)
  if (selectedIndex.value !== null) {
    if (selectedIndex.value === index) selectedIndex.value = fields.value.length ? Math.max(0, index - 1) : null
    else if (selectedIndex.value > index) selectedIndex.value--
  }
  markDirty()
}

function move(index: number, delta: number) {
  const target = index + delta
  if (target < 0 || target >= fields.value.length) return
  const [item] = fields.value.splice(index, 1)
  fields.value.splice(target, 0, item)
  if (selectedIndex.value === index) selectedIndex.value = target
  markDirty()
}

function onLabelBlur() {
  if (selected.value && !selected.value.key && selected.value.label) {
    selected.value.key = camelCase(selected.value.label)
  }
}

function onTypeChange() {
  if (!selected.value) return
  if (selected.value.type === 'singleSelect' || selected.value.type === 'multiSelect') {
    selected.value.allowedValues ??= []
  } else {
    selected.value.allowedValues = null
  }
  selected.value.defaultValue = null
  markDirty()
}

const saveMutation = useMutation({
  mutationFn: () =>
    api<{ version: number; changed: boolean }>(`/api/projects/${projectId}/schema`, {
      method: 'PUT',
      body: { fields: fields.value },
    }),
  onSuccess: (result) => {
    dirty.value = false
    serverErrors.value = []
    toast.success(result.changed ? `Schema saved — now at version ${result.version}.` : 'No changes to save.')
    queryClient.invalidateQueries({ queryKey: ['project', projectId] })
    queryClient.invalidateQueries({ queryKey: ['project', projectId, 'schema'] })
  },
  onError: (error) => {
    if (error instanceof ApiError && error.errors) {
      serverErrors.value = Object.entries(error.errors).flatMap(([field, messages]) =>
        messages.map((m) => `${field}: ${m}`),
      )
    } else {
      toast.error('Could not save the schema.')
    }
  },
})

onBeforeRouteLeave(() => {
  if (dirty.value && !confirm('You have unsaved schema changes. Leave anyway?')) return false
})
</script>

<template>
  <SkeletonRows v-if="schemaQuery.isPending.value" :rows="6" />

  <div v-else class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <p class="text-sm text-muted">
        Define the fields the AI must extract from each record's text.
        <span v-if="dirty" class="font-medium text-warning">Unsaved changes.</span>
      </p>
      <BaseButton
        :loading="saveMutation.isPending.value"
        :disabled="isArchived || !dirty"
        @click="saveMutation.mutate()"
      >
        Save Schema
      </BaseButton>
    </div>

    <div
      v-if="serverErrors.length"
      class="rounded-md border border-danger/40 bg-danger-soft px-3 py-2 text-sm text-danger"
      role="alert"
    >
      <p class="font-medium">The schema has problems:</p>
      <ul class="ml-4 list-disc">
        <li v-for="(error, i) in serverErrors" :key="i">{{ error }}</li>
      </ul>
    </div>

    <div class="grid gap-4 lg:grid-cols-[260px_1fr_minmax(260px,320px)]">
      <!-- Field list -->
      <section class="rounded-xl border border-border bg-surface p-3">
        <EmptyState
          v-if="fields.length === 0"
          title="No fields yet"
          description="Add your first output field."
        >
          <template #icon>🧩</template>
          <template #action>
            <BaseButton v-if="!isArchived" variant="secondary" @click="addField">+ Add Field</BaseButton>
          </template>
        </EmptyState>
        <template v-else>
          <ul class="flex flex-col gap-1">
            <li v-for="(field, index) in fields" :key="index">
              <button
                class="flex w-full items-center justify-between gap-1 rounded-md px-2.5 py-2 text-left text-sm hover:bg-bg"
                :class="selectedIndex === index ? 'bg-primary-soft text-primary' : 'text-text'"
                @click="selectedIndex = index"
              >
                <span class="truncate">
                  {{ field.label || field.key || '(new field)' }}
                  <span v-if="field.required" class="text-danger">*</span>
                </span>
                <span class="shrink-0 text-xs text-muted">{{ FIELD_TYPE_LABELS[field.type] }}</span>
              </button>
              <div v-if="selectedIndex === index && !isArchived" class="flex gap-1 px-2 pb-1">
                <button class="text-xs text-muted hover:text-text" :disabled="index === 0" @click="move(index, -1)">↑ up</button>
                <button class="text-xs text-muted hover:text-text" :disabled="index === fields.length - 1" @click="move(index, 1)">↓ down</button>
                <button class="ml-auto text-xs text-danger/80 hover:text-danger" @click="removeField(index)">delete</button>
              </div>
            </li>
          </ul>
          <BaseButton v-if="!isArchived" variant="ghost" class="mt-2 w-full" @click="addField">+ Add Field</BaseButton>
        </template>
      </section>

      <!-- Field editor -->
      <section class="rounded-xl border border-border bg-surface p-4">
        <p v-if="!selected" class="text-sm text-muted">Select a field to edit it.</p>
        <div v-else class="flex flex-col gap-4" @input="markDirty">
          <div class="grid gap-4 sm:grid-cols-2">
            <BaseInput
              v-model="selected.label"
              label="Label"
              required
              :disabled="isArchived"
              placeholder="e.g. Incident Date"
              @blur="onLabelBlur"
            />
            <BaseInput
              v-model="selected.key"
              label="Key"
              required
              :disabled="isArchived"
              placeholder="e.g. incidentDate"
            />
          </div>
          <div class="grid gap-4 sm:grid-cols-2">
            <BaseSelect
              :model-value="selected.type"
              label="Type"
              :options="typeOptions"
              :disabled="isArchived"
              @update:model-value="(v) => { selected!.type = v as FieldType; onTypeChange() }"
            />
            <label class="mt-6 flex cursor-pointer items-center gap-2 text-sm text-text">
              <input
                v-model="selected.required"
                type="checkbox"
                class="h-4 w-4 accent-[var(--c-primary)]"
                :disabled="isArchived"
                @change="markDirty"
              />
              Required
            </label>
          </div>
          <BaseInput
            v-model="selected.description as string"
            label="Description"
            :disabled="isArchived"
            placeholder="Shown to reviewers under the field"
          />
          <div class="flex flex-col gap-1.5">
            <label class="text-sm font-medium text-text">Extraction instruction</label>
            <textarea
              v-model="selected.extractionInstruction as string"
              rows="3"
              dir="auto"
              :disabled="isArchived"
              placeholder="Tell the AI exactly how to extract this field, e.g. 'Convert Persian dates to ISO 8601.'"
              class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
            />
          </div>
          <div
            v-if="selected.type === 'singleSelect' || selected.type === 'multiSelect'"
            class="flex flex-col gap-1.5"
          >
            <label class="text-sm font-medium text-text">Allowed values <span class="text-danger">*</span></label>
            <textarea
              v-model="allowedValuesText"
              rows="4"
              dir="auto"
              :disabled="isArchived"
              placeholder="One value per line"
              class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
            />
            <p class="text-xs text-muted">One value per line. The AI may only pick from these.</p>
          </div>
        </div>
      </section>

      <!-- Preview -->
      <section class="rounded-xl border border-border bg-surface p-4">
        <div class="mb-3 flex gap-1 rounded-md bg-bg p-0.5">
          <button
            v-for="tab in (['form', 'json'] as const)"
            :key="tab"
            class="flex-1 rounded px-2 py-1 text-xs font-medium"
            :class="previewTab === tab ? 'bg-surface text-text shadow-sm' : 'text-muted'"
            @click="previewTab = tab"
          >
            {{ tab === 'form' ? 'Form Preview' : 'JSON Shape' }}
          </button>
        </div>
        <DynamicForm v-if="previewTab === 'form'" v-model="previewModel" :fields="fields" />
        <pre v-else class="overflow-x-auto rounded-md bg-bg p-3 text-xs text-text">{{ jsonShape }}</pre>
      </section>
    </div>
  </div>
</template>
