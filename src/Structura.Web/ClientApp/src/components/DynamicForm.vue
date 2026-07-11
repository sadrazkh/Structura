<script setup lang="ts">
import { computed } from 'vue'
import type { FieldSpec } from '../api/types'

const props = withDefaults(
  defineProps<{
    fields: FieldSpec[]
    disabled?: boolean
  }>(),
  { disabled: false },
)

const model = defineModel<Record<string, unknown>>({ default: () => ({}) })

const orderedFields = computed(() =>
  [...props.fields].sort((a, b) => a.displayOrder - b.displayOrder),
)

function get(key: string): unknown {
  return model.value[key]
}

function set(key: string, value: unknown) {
  model.value = { ...model.value, [key]: value }
}

function asString(value: unknown): string {
  return value === null || value === undefined ? '' : String(value)
}

function setNumber(field: FieldSpec, raw: string) {
  if (raw.trim() === '') {
    set(field.key, null)
    return
  }
  const parsed = field.type === 'integer' ? parseInt(raw, 10) : parseFloat(raw)
  set(field.key, Number.isNaN(parsed) ? raw : parsed)
}

function multiValues(key: string): string[] {
  const value = get(key)
  return Array.isArray(value) ? value.map(String) : []
}

function toggleMulti(field: FieldSpec, option: string, checked: boolean) {
  const current = multiValues(field.key)
  set(field.key, checked ? [...current, option] : current.filter((v) => v !== option))
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <p v-if="orderedFields.length === 0" class="text-sm text-muted">
      No fields defined yet.
    </p>

    <div v-for="field in orderedFields" :key="field.key" class="flex flex-col gap-1.5">
      <label class="text-sm font-medium text-text">
        {{ field.label || field.key }}<span v-if="field.required" class="text-danger"> *</span>
      </label>
      <p v-if="field.description" class="-mt-1 text-xs text-muted" dir="auto">{{ field.description }}</p>

      <!-- shortText -->
      <input
        v-if="field.type === 'shortText'"
        :value="asString(get(field.key))"
        :disabled="disabled"
        dir="auto"
        type="text"
        class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
        @input="set(field.key, ($event.target as HTMLInputElement).value)"
      />

      <!-- longText -->
      <textarea
        v-else-if="field.type === 'longText'"
        :value="asString(get(field.key))"
        :disabled="disabled"
        dir="auto"
        rows="4"
        class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
        @input="set(field.key, ($event.target as HTMLTextAreaElement).value)"
      />

      <!-- integer / decimal -->
      <input
        v-else-if="field.type === 'integer' || field.type === 'decimal'"
        :value="asString(get(field.key))"
        :disabled="disabled"
        type="number"
        :step="field.type === 'integer' ? 1 : 'any'"
        class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
        @input="setNumber(field, ($event.target as HTMLInputElement).value)"
      />

      <!-- boolean -->
      <label
        v-else-if="field.type === 'boolean'"
        class="flex w-fit cursor-pointer items-center gap-2 text-sm text-text"
      >
        <input
          type="checkbox"
          :checked="get(field.key) === true"
          :disabled="disabled"
          class="h-4 w-4 accent-[var(--c-primary)]"
          @change="set(field.key, ($event.target as HTMLInputElement).checked)"
        />
        {{ get(field.key) === true ? 'Yes' : 'No' }}
      </label>

      <!-- date -->
      <input
        v-else-if="field.type === 'date'"
        :value="asString(get(field.key))"
        :disabled="disabled"
        type="date"
        class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
        @input="set(field.key, ($event.target as HTMLInputElement).value || null)"
      />

      <!-- singleSelect -->
      <select
        v-else-if="field.type === 'singleSelect'"
        :value="asString(get(field.key))"
        :disabled="disabled"
        class="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
        @change="set(field.key, ($event.target as HTMLSelectElement).value || null)"
      >
        <option value="">—</option>
        <option v-for="option in field.allowedValues ?? []" :key="option" :value="option">
          {{ option }}
        </option>
      </select>

      <!-- multiSelect -->
      <div v-else-if="field.type === 'multiSelect'" class="flex flex-wrap gap-x-4 gap-y-1.5">
        <label
          v-for="option in field.allowedValues ?? []"
          :key="option"
          class="flex cursor-pointer items-center gap-1.5 text-sm text-text"
        >
          <input
            type="checkbox"
            :checked="multiValues(field.key).includes(option)"
            :disabled="disabled"
            class="h-4 w-4 accent-[var(--c-primary)]"
            @change="toggleMulti(field, option, ($event.target as HTMLInputElement).checked)"
          />
          <span dir="auto">{{ option }}</span>
        </label>
      </div>
    </div>
  </div>
</template>
