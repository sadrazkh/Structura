<script setup lang="ts">
withDefaults(
  defineProps<{
    label?: string
    type?: string
    placeholder?: string
    error?: string
    autocomplete?: string
    required?: boolean
    disabled?: boolean
  }>(),
  { type: 'text' },
)

const model = defineModel<string>({ default: '' })
const id = `input-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <label v-if="label" :for="id" class="text-sm font-medium text-text">
      {{ label }}<span v-if="required" class="text-danger"> *</span>
    </label>
    <input
      :id="id"
      v-model="model"
      :type="type"
      :placeholder="placeholder"
      :autocomplete="autocomplete"
      :disabled="disabled"
      dir="auto"
      class="w-full rounded-md border bg-surface px-3 py-2 text-sm text-text placeholder:text-muted disabled:opacity-60"
      :class="error ? 'border-danger' : 'border-border'"
      :aria-invalid="!!error"
    />
    <p v-if="error" class="text-xs text-danger">{{ error }}</p>
  </div>
</template>
