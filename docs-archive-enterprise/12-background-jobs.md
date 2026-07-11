# 12 — Background Job Design

Technology: **Hangfire + PostgreSQL storage** (decision D3). Domain state tables are the source of truth; Hangfire executes. All job classes live in their owning module under `Infrastructure/Jobs`, are registered as DI services, and receive only IDs + correlation ID as arguments (payloads re-read from DB).

## Global rules

- **Idempotency:** every job re-reads domain state first; "already done / already terminal" ⇒ log + exit success. Writes guarded by unique constraints and status-transition guards (doc 09).
- **Heartbeat:** long-running jobs update `heartbeat_at` every 15 s.
- **Recovery:** `JobRecoveryService` (IHostedService; on startup + every 2 min): domain tasks `Running` with `heartbeat_at < now()-2min` → back to `Queued` + re-enqueue; runs in `Running` with zero live tasks and unfinished counters → re-enqueue orchestrator; deliveries stuck `Sending` → `Pending`.
- **Cancellation:** cooperative — jobs check `cancel_requested`/CancellationToken between units of work (rows, records, pages).
- **Dead-letter:** domain-level `DeadLettered` status after max attempts (Hangfire's own retry set to 0 for domain-retried jobs to avoid double retry layers — retries are explicit in domain logic; infrastructure exceptions before domain start use Hangfire retry ×3).
- **Logging/metrics:** every job logs start/end/duration with correlation ID + domain IDs; counters exposed via meters (`structura.jobs.<name>.{started,succeeded,failed,duration}`).
- **Queues:** Hangfire queues `default`, `processing` (extraction tasks), `delivery`, `maintenance` — one server, worker counts: processing 16, default 8, delivery 4, maintenance 2.

## Job catalog

### 1. FileImportJob — `Ingestion`
| | |
|---|---|
| Input | `importRunId` |
| Output | records inserted; run counters/status; `import_row_errors` |
| Trigger | `POST imports/{id}/start` |
| Retry | none automatic (file parse determinism); infra-fail → run `Failed`, admin restarts manually |
| Timeout | soft 60 min (heartbeat-monitored) |
| Idempotency | resumes by `imported_count` offset? No — simpler: on re-run after crash, skips rows whose `(project_id, external_id)` already exist (dup-skip), auto-ID rows deduped via deterministic row hash `(run_id, row_number)` stored as external_id suffix for generated IDs |
| Concurrency | 1 per run (Hangfire `DisableConcurrentExecution` on runId); multiple runs in parallel allowed |
| Cancellation | checked per 500-row chunk → `Cancelled` |
| Dead-letter | n/a (run `Failed` with error) |

### 2. ConnectorSyncJob — `Ingestion`
Input: `inputConnectorId` (+ manual/scheduled flag). Fetches pages via SafeHttp, maps rows (JSONPath), dedupes on external ID, inserts records, creates an `ImportRun` (`source_type=Connector`) for observability, advances `sync_checkpoint` (cursor/date) **after** each committed page (checkpointing). Retry: per-page HTTP retries (3, backoff); job-level failure keeps checkpoint (resume-safe). Schedule: Hangfire recurring job per enabled connector (`RecurringJob.AddOrUpdate(cron)`); overlapping runs skipped (`DisableConcurrentExecution`). Cancellation: between pages. Dead-letter: run marked `Failed`, connector `last_run` error surfaced + admin notification.

### 3. ProcessingRunOrchestratorJob — `Processing`
Input: `runId`. Loop: check run status (`pause_requested` → set `Paused`, exit; resumed runs re-enqueue orchestrator; `cancel_requested` → mark queued tasks `Cancelled`, drain, finalize) → pick next `Queued` tasks up to `concurrency` in-flight → enqueue `RecordExtractionJob` per task (queue `processing`) → budget/error-rate checks on counters → when no queued/running tasks remain: finalize run status (`Completed` / `CompletedWithErrors` / `StoppedBy*`), fire completion notification (admin + Telegram), SignalR final. Re-entrant: enqueues itself every 5 s while active (polling loop, cheap) — simple, restart-proof. Idempotent by state.

### 4. RecordExtractionJob — `Processing`
Input: `taskId`. Steps: load task+run (terminal ⇒ exit) → task `Running` + heartbeat → record `Processing` → run extraction pipeline (doc 13) with per-attempt provider retries (429/5xx/timeout: max_retries, exp backoff + jitter; RateGate for RPM/TPM/concurrency) → write `extraction_results` (unique `(record_id, run_id, attempt)`) → update record statuses per outcome → usage event + run counters (atomic `UPDATE … SET succeeded = succeeded+1`) → task terminal + SignalR progress (throttled batch, ≥1/s per run).
Retry: domain-managed inside the job (provider retries); a failed task can be re-run only via Retry-Failed child run. Timeout: per-request `provider.request_timeout`; job soft cap 10 min. Cancellation: token checked before provider call; in-flight HTTP honors CT. Dead-letter: `Failed` task with `last_error_code`; repeated infra-crash (3 Hangfire re-executions) → `DeadLettered`.

### 5. AssignmentDistributionJob — `Reviews`
Input: `batchId, scopeSnapshot, reviewerIds, strategy`. Used when batch > 1,000 records (smaller batches run synchronously in the handler). Chunks of 500: create assignments + update records + notifications (aggregated: one notification per reviewer at end). Idempotent: partial unique `(record_id) WHERE Active` makes duplicates impossible; re-run continues remainder. Cancellation supported; progress via SignalR.

### 6. ExcelExportJob — `Delivery`
Input: `exportRunId`. Streams query (keyset, 1,000-row pages) → MiniExcel streaming writer → temp file → atomic move to `/data/exports` → run `Completed` (+file size/rows, `file_expires_at`) → notification. Sanitizes formula injection on every string cell (prefix `'` when first char ∈ `= + - @ \t \r`). Retry: manual only. Cancellation: between pages; partial temp file deleted. Failure: run `Failed` + error.

### 7. DeliveryRunJob + ApiDeliveryJob — `Delivery`
DeliveryRunJob (input `deliveryRunId`): creates/refreshes `api_deliveries` rows for scope (skipping `Delivered` per D15), then enqueues ApiDeliveryJob per row (queue `delivery`, respecting connector `mode`: batch mode groups N records per request into one delivery row set with shared attempt). Monitors completion → run terminal + notification.
ApiDeliveryJob (input `apiDeliveryId`): status `Pending`→`Sending` → render payload (template + mapping) → SafeHttp send with `Idempotency-Key` → classify (doc 09 §7): success → `Delivered` + `external_id` (responseIdPath) → record delivery status update; retryable → `Pending` + `next_retry_at` (delayed Hangfire enqueue); permanent → `Failed`. Attempts logged in `delivery_attempts` (excerpts ≤4 KB, secrets redacted). Max attempts (connector retryPolicy, default 3) → `Failed`; admin Retry re-arms; second exhaustion → `DeadLettered` + admin notification.

### 8. NotificationDispatchJob — `Notifications`
Input: `notificationId`. In-app row already exists; this job handles Telegram send for linked users (bot API via SafeHttp/proxy). Retry ×3 backoff; failure → `telegram_status=Failed` (in-app remains). Batch variant for assignment batches (one aggregated message per reviewer).

### 9. CleanupJob — `maintenance` (recurring, hourly + daily parts)
Hourly: clear expired record locks; expire link codes; mark `Abandoned` import runs (>24 h pre-mapping); requeue overdue `Pending` deliveries. Daily 03:00 UTC: null `raw_response` past retention (R12); delete expired export files + mark `FileExpired`; purge read notifications > 90 days; delete uploaded import files > 7 days after run end; optional audit pruning per settings. Each sub-task independent try/catch + metrics.

### 10. TestCaseRunJob — `Prompts`
Input: `testCaseId` list + versions/model. Executes pipeline per case sequentially (respecting RateGate), stores `test_case_runs`, computes pass/diff vs expected. Used by Run All (sync for ≤5 cases).

## Recurring registry (configured at startup)
| Job | Cron |
|---|---|
| CleanupJob (hourly part) | `0 * * * *` |
| CleanupJob (daily part) | `0 3 * * *` |
| ConnectorSyncJob | per-connector cron |
| JobRecoveryService | hosted-service timer 2 min (not Hangfire) |
| TelegramPollingService | hosted service (only when mode=polling) |
