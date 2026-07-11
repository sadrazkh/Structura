<script setup lang="ts">
defineProps<{
  label?: string
  options: { value: string; label: string }[]
  error?: string
  required?: boolean
  disabled?: boolean
}>()

const model = defineModel<string>({ default: '' })
const id = `select-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <label v-if="label" :for="id" class="text-sm font-medium text-text">
      {{ label }}<span v-if="required" class="text-danger"> *</span>
    </label>
    <select
      :id="id"
      v-model="model"
      :disabled="disabled"
      class="w-full rounded-md border bg-surface px-3 py-2 text-sm text-text disabled:opacity-60"
      :class="error ? 'border-danger' : 'border-border'"
    >
      <option v-for="option in options" :key="option.value" :value="option.value">
        {{ option.label }}
      </option>
    </select>
    <p v-if="error" class="text-xs text-danger">{{ error }}</p>
  </div>
</template>
