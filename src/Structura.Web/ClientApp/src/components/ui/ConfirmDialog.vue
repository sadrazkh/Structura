<script setup lang="ts">
import BaseDialog from './BaseDialog.vue'
import BaseButton from './BaseButton.vue'

withDefaults(
  defineProps<{
    title: string
    message: string
    confirmLabel?: string
    danger?: boolean
    loading?: boolean
  }>(),
  { confirmLabel: 'Confirm', danger: false, loading: false },
)

const open = defineModel<boolean>({ default: false })
const emit = defineEmits<{ confirm: [] }>()
</script>

<template>
  <BaseDialog v-model="open" :title="title">
    <p class="text-sm text-muted">{{ message }}</p>
    <template #footer>
      <BaseButton variant="secondary" @click="open = false">Cancel</BaseButton>
      <BaseButton :variant="danger ? 'danger' : 'primary'" :loading="loading" @click="emit('confirm')">
        {{ confirmLabel }}
      </BaseButton>
    </template>
  </BaseDialog>
</template>
