<script setup lang="ts">
withDefaults(
  defineProps<{ variant?: 'neutral' | 'success' | 'danger' | 'warning' | 'info' | 'primary' }>(),
  { variant: 'neutral' },
)

const classes: Record<string, string> = {
  neutral: 'bg-bg text-muted border-border',
  success: 'bg-success-soft text-success border-transparent',
  danger: 'bg-danger-soft text-danger border-transparent',
  warning: 'bg-warning-soft text-warning border-transparent',
  info: 'bg-info-soft text-info border-transparent',
  primary: 'bg-primary-soft text-primary border-transparent',
}
</script>

<template>
  <span
    class="inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium"
    :class="classes[variant]"
  >
    <slot />
  </span>
</template>
