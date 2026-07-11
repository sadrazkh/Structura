export type FieldType =
  | 'shortText' | 'longText' | 'integer' | 'decimal'
  | 'boolean' | 'date' | 'singleSelect' | 'multiSelect'

export interface FieldSpec {
  key: string
  label: string
  type: FieldType
  required: boolean
  description?: string | null
  extractionInstruction?: string | null
  allowedValues?: string[] | null
  defaultValue?: unknown
  displayOrder: number
}

export const FIELD_TYPE_LABELS: Record<FieldType, string> = {
  shortText: 'Short Text',
  longText: 'Long Text',
  integer: 'Integer',
  decimal: 'Decimal',
  boolean: 'Boolean',
  date: 'Date',
  singleSelect: 'Single Select',
  multiSelect: 'Multi Select',
}

export interface RecordListItem {
  id: string
  externalId: string
  textExcerpt: string
  processingStatus: 'Pending' | 'Processing' | 'Completed' | 'Failed'
  reviewStatus: 'Unassigned' | 'Assigned' | 'InReview' | 'Approved' | 'Rejected' | 'ReprocessRequested'
  deliveryStatus: 'Pending' | 'Delivered' | 'Failed'
  reviewerId: string | null
  updatedAt: string
}

export interface ImportRunSummary {
  id: string
  source: string
  fileName: string | null
  status: 'AwaitingMapping' | 'Running' | 'Completed' | 'CompletedWithErrors' | 'Failed' | 'Cancelled'
  totalRows: number | null
  imported: number
  skippedDuplicates: number
  failed: number
  createdAt: string
  finishedAt: string | null
}

export const STATUS_BADGE: Record<string, 'neutral' | 'success' | 'danger' | 'warning' | 'info' | 'primary'> = {
  Pending: 'neutral',
  Processing: 'info',
  Completed: 'success',
  CompletedWithErrors: 'warning',
  Failed: 'danger',
  Cancelled: 'neutral',
  Running: 'info',
  AwaitingMapping: 'warning',
  Unassigned: 'neutral',
  Assigned: 'info',
  InReview: 'primary',
  Approved: 'success',
  Rejected: 'danger',
  ReprocessRequested: 'warning',
  Delivered: 'success',
}
