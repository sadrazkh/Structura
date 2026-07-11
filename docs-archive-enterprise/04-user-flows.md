# 04 — Complete User Flows

Every flow lists actor steps and system behavior. Status names refer to [09-state-machines.md](09-state-machines.md). All flows are MVP scope.

### F1. System admin creates the first account
1. Operator sets `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` in `.env`, starts Compose.
2. On startup, after migrations, `IdentitySeeder` checks: if no user with System Administrator role exists → creates the user (email confirmed, `must_change_password = true`), seeds roles/permissions, writes audit event `identity.bootstrap_admin_created`.
3. Admin signs in at `/signin`; is forced through the Change Password screen before anything else.
4. If bootstrap vars are absent and no admin exists, the app serves a setup-required page (API returns `503 setup_required` for other routes) and logs instructions.

### F2. Admin creates users and reviewers
1. Admin → Users → **New User**: full name, email (username), initial password (generated or typed), active flag.
2. Optional global role (System Administrator / Auditor). Reviewers get no global role.
3. System validates email uniqueness, password policy (min 10 chars, not top-10k common), creates user with `must_change_password = true`, audits `identity.user_created`.
4. Admin communicates credentials out-of-band. First login forces password change.
5. Project access is granted separately: Project → Members → **Add Member** → pick user + project role (Project Administrator / Operations Manager / Reviewer).

### F3. Admin creates a project
1. Admin → Projects → **New Project** → Create Project Wizard (5 steps, R10). Step 1: name (unique, ≤120 chars), description.
2. **Save & Continue Later** at any step keeps the project in `Draft` status (visible in Projects list with a Draft badge; wizard resumes where left off).
3. Finishing the wizard sets project `Active`. Creator becomes Project Administrator automatically. Audit `projects.created`.

### F4. Admin defines a dynamic schema
1. Wizard step 2 (or Project → Schema later): Schema Builder opens with an empty field list.
2. Admin adds fields: picks type, sets key (auto-suggested from label, camelCase, validated unique per level), label, description, required/nullable, default, extraction instruction, examples, allowed values, validation (min/max/regex/length), confidence threshold, review requirement, visibility, order, group, export config.
3. For `object` / `objectList`: admin adds child fields recursively (same editor, nested panel). `Person` / `Location` template buttons insert predefined child structures.
4. Live JSON preview panel shows the resulting output shape; a sample dynamic form preview renders next to it.
5. Save → persists to the current **Draft** schema version. Validation on save: unique keys, valid regexes, allowedValues type-consistent, no reserved keys (`_meta`), depth ≤ 4, fields ≤ 150.

### F5. Admin publishes a schema version
1. Project → Schema → **Publish**: dialog shows version number (auto-increment), diff summary vs previous published version (added/removed/changed fields), and a warning if records already exist.
2. Confirm → Draft becomes `Published` (immutable), becomes the project's active schema; a fresh Draft copy is created for further edits. Audit `schemas.version_published`.
3. Running processing runs keep their pinned older version (D8). New runs use the new version.

### F6. Admin configures OpenRouter or NVIDIA
1. System Admin → AI Providers → **New Provider**: type (OpenRouter / NVIDIA / OpenAI-compatible), name, base URL (prefilled per type), API key, default model, available models (text list or "fetch models" for OpenRouter), timeout, retries, temperature/topP/max tokens defaults, concurrency, RPM/TPM, custom headers, optional proxy.
2. **Test Connection** → server sends a minimal completion request; shows latency, model echo, token counts, or the error. Nothing saved until Save.
3. Save → API key encrypted (ISecretProtector), masked afterwards. Audit `providers.created` (key value never audited).
4. In the project (wizard step 3 or Project → AI Configuration): pick provider + model + generation settings overrides.

### F7. Admin creates extraction instructions
1. Project → Prompt Configuration (wizard step 3 includes a compact version): system instruction, general extraction instruction, input template (`{{record.text}}`, `{{record.metadata.<key>}}` placeholders, validated), context field selection, output language (`auto` | `en` | `fa` | source-language), missing/ambiguous/unknown value behaviors, strict JSON toggle (default on), few-shot examples (input + expected JSON pairs, validated against schema), reference context block.
2. Saves to the Draft prompt version; **Publish** works exactly like schema publish (F5).

### F8. Admin tests extraction in Playground
1. Project → Playground: paste sample text (+ optional context values), pick provider/model (defaults from project), pick schema/prompt version (defaults: active published; may select Draft, clearly labelled).
2. **Run Test** → server executes the full real pipeline (prompt build → provider call → parse → repair → validate) synchronously (timeout 120 s) and returns: generated prompt (system + user messages), raw response, parsed JSON, validation result, rendered dynamic form preview, token usage, estimated cost, duration, error details.
3. **Save as Test Case** stores input/context (+ optional expected output). Test Cases page lists them; **Run All** re-executes and shows pass/fail vs expected output (normalized comparison) and diffs vs last run.

### F9. Admin uploads an Excel file
1. Project → Input Sources → Excel Import → drag/drop `.xlsx`/`.csv` (≤100 MB). Client validates extension; server validates MIME, extension, size; stores to `/data/uploads`; parses first 50 rows for preview.
2. UI shows sheet picker (XLSX), header row toggle, encoding picker (CSV: UTF-8 default), and the preview grid.

### F10. Admin maps ID and text columns
1. Mapping panel: **Record ID column** (optional — "generate IDs" option), **Text column** (required), metadata columns (multi-select → stored in `input_metadata`), context columns (available to prompt template).
2. Live validation on the preview: duplicate IDs, empty IDs, empty text, oversized text — shown as per-column warnings with counts.
3. **Duplicate policy** select: Skip duplicates (default) / Fail row. Duplicates = same `external_id` already in project or repeated in file.
4. **Start Import** → creates `ImportRun` (status `Mapped` → `Importing`), enqueues `FileImportJob`.

### F11. System imports records
1. `FileImportJob` streams rows in 500-row chunks; each valid row inserts a `Record` (`processing_status = Imported`, `review_status = NotReady`, `delivery_status = NotReady`).
2. Row failures (empty text, bad ID, duplicate under Fail policy, cell too large) become `import_row_errors` rows; the job continues.
3. Progress (rows read/imported/skipped/failed) streams via SignalR `ImportProgress`; run ends `Completed` / `CompletedWithErrors` / `Failed` / `Cancelled`. Row errors downloadable as CSV. Cancel button stops at the next chunk boundary.

### F12. Admin starts processing all records
1. Project → Records: filter (default `processing_status = Imported`), or Processing → **New Run**.
2. Scope choices: selected records / next N / all unprocessed / all matching filter / retry failed / reprocess rejected / reprocess low-confidence / reprocess with another model. Approved records excluded unless explicitly included (R13).
3. Pre-run summary: record count, estimated input/output tokens, estimated cost (price table + chars-per-token ratios), model, concurrency, budget impact vs remaining budgets. Runs above the confirmation threshold (default $10, configurable) require typing the project name.
4. **Start** → `ProcessingRun` (`Queued`) with `config_snapshot`; per-record `processing_tasks` created (`Queued`); records flip to `Queued`; orchestrator job enqueued.

### F13. AI processes records in background
For each task (parallelism = run concurrency, gated by provider RateGate):
1. Task → `Running`, heartbeat starts; record → `Processing`.
2. Pipeline: build prompt → call provider (with per-request timeout, retries with exponential backoff + jitter on 429/5xx/timeout) → parse → deterministic repair if needed → validate against schema.
3. Success → `extraction_results` row; record `Processed` (or `ValidationFailed`); `review_status` → per review policy (`Unassigned` or auto-approved path). Usage event written (tokens, cost).
4. Failure after retries → task `Failed`, record `ProcessingFailed`, error stored. Rate-limit pauses are logged, not failures.
5. Budget check before/after each task (R27); breach → run `StoppedByBudget`, remaining tasks `Cancelled`.

### F14. Admin monitors progress
1. Processing → Run Details: live counters (queued/running/succeeded/failed/cancelled), progress bar, throughput (records/min), token+cost accumulation, error breakdown by code, per-task table (filterable), all via SignalR `ProcessingProgress` with polling fallback.
2. Pause → no new tasks start (in-flight finish); Resume; Cancel → queued tasks cancelled, in-flight finish; all audited.

### F15. Failed records are retried
1. Run Details → **Retry Failed** (or Records filter `ProcessingFailed` → bulk action): creates a child run scoped to failed records (same snapshot by default; admin may pick another model — new snapshot).
2. Tasks re-execute; attempt counter increments; previous extraction attempts remain stored.

### F16. Processed records become ready for review
1. On extraction success with `ReviewAll` policy: `review_status = Unassigned`. Dashboard "Ready for review / Unassigned" counters update in real time.
2. With threshold policy: qualifying records auto-approve (D20) → `delivery_status = ReadyForExport`; the rest go `Unassigned`.

### F17. Admin assigns 500 records between five reviewers
1. Project → Assignments → **New Assignment Batch**: filter (default `review_status = Unassigned`), select-all-matching (500), choose reviewers (5), strategy: distribute evenly / by count per reviewer / round-robin; set priority + optional due date.
2. Preview: 100 records per reviewer. Confirm → `AssignmentBatch` + 500 `review_assignments` rows (`Active`); records → `Assigned` with `assigned_reviewer_id`; `AssignmentDistributionJob` handles >1k batches asynchronously with progress.
3. Notifications created per reviewer (in-app + Telegram if linked). Audit `assignments.batch_created`.

### F18. Reviewer receives Telegram notification
1. `NotificationDispatchJob` sends: "You have 100 new records to review in *Incident Reports*." with inline buttons **Open Mini App** (deep link `t.me/<bot>/review?startapp=p_<projectId>`) and **Show my tasks** (`/tasks`).
2. Delivery failures retry (3 attempts); permanent failure marks notification `telegram_failed` (in-app copy always exists).

### F19. Reviewer opens Mini App
1. Tap Open Mini App → Telegram loads `/tg`. The adapter validates `initData` server-side (HMAC, `auth_date` ≤ 24h), maps `telegram_user_id` → linked user, issues JWT, renders **My Tasks**.
2. Unlinked Telegram account → linking instruction screen (F-link below).

### F20. Reviewer reviews records sequentially
1. My Tasks → project card → **Start Reviewing** → Focus Review opens the first record by priority/due date/FIFO within the reviewer's queue.
2. Opening acquires the soft lock and marks assignment `started_at` (first time), record `InReview`.
3. Layout: original text (left, `dir="auto"`), metadata/context, dynamic form (right) prefilled with AI values (or draft if exists), validation issues, confidence indicators, evidence quotes, actions.
4. After Approve/Reject the next queued record loads automatically (prefetched).

### F21. Reviewer edits an incorrect AI value
1. Reviewer edits any editable field; the field gets a "modified" dot; original AI value shown on hover/expand with one-click revert.
2. Client-side validation runs live; server revalidates on every save/approve.

### F22. Reviewer saves a draft
1. **Save Draft** (or auto-save every 30 s when dirty): `PUT /records/{id}/review/draft` with `baseVersion`; server verifies lock + version, stores `draft_output`, record → `DraftSaved`.
2. Offline: draft persists to IndexedDB and syncs on reconnect (D17).

### F23. Reviewer approves the record
1. **Approve** → server checks: lock owned, version match, required fields present, zero validation errors, status valid.
2. Transaction: `record_reviews.final_output` + `field_changes` diff computed (D18) → record `Approved`, `delivery_status = ReadyForExport`, assignment `Completed` (duration recorded), lock released, audit + review event.
3. UI advances to next record; progress bar increments.

### F24. Reviewer rejects another record
1. **Reject** → dialog requires a note (reason). Server sets record `Rejected`, assignment `Completed`, stores note.
2. Rejected records surface in the admin Records filter and Review Operations dashboard; admin decides: reprocess (F15-style scope "reprocess rejected") or reassign.

### F25. Reviewer returns a record for reprocessing
1. **Return for Reprocessing** → dialog with note (e.g. "text truncated"). Record: `review_status = ReturnedForReprocessing`, `processing_status = NeedsReprocessing`.
2. Such records appear in admin processing scopes ("needs reprocessing"). After reprocessing, R19 applies (back to same reviewer if assignment still Active — return keeps it Active).

### F26. Reviewer opens Table Mode
1. Toggle Focus/Table in Review Queue. Table shows assigned records: configurable columns (chosen schema fields + status/validation/confidence), pagination, filter by status/validation.
2. Inline editing for scalar fields; complex fields via drawer (D23). Each row save uses the same lock+version-checked draft endpoint. Click-through to Focus Mode per row.

### F27. Reviewer bulk-approves valid records
1. Multi-select rows → **Bulk Approve** (visible only if project `allowBulkApprove`).
2. Server processes each record independently against the R20 safeguards; response lists per-record success/failure reasons; UI shows summary ("38 approved, 2 skipped: validation errors").
3. Bulk Reject and Bulk Return work the same way (note applied to all).

### F28. Admin monitors reviewer progress
1. Review Operations dashboard: backlog totals, per-reviewer pending/completed today/avg review time/edit rate/rejection rate, oldest pending, Telegram link status. Includes the fairness disclaimer (record difficulty varies).
2. Drill into a reviewer → their assignment list with statuses.

### F29. Admin reassigns unfinished records
1. Assignments → filter `Active`, select records or whole batch remainder → **Reassign** to another reviewer (or **Unassign**).
2. Server: old assignment `Reassigned`, new `Active` created; record's `assigned_reviewer_id` updated; drafts are preserved (draft belongs to record, flagged with author); locked records require **Release Lock** first (admin action, audited). Both reviewers notified.

### F30. Admin exports approved records to Excel
1. Export Center → **New Export**: filter (default `review_status ∈ {Approved}`, date range, batch), column picker (record columns, schema fields incl. child-sheet groups, review/processing metadata columns), column order drag-and-drop; settings persist per project as the default template.
2. **Start Export** → `ExportRun` + `ExcelExportJob` streams the file (D11, D23), sanitizes formula injection, stores to `/data/exports`.
3. Completion → notification + download button (authenticated URL). Runs listed with size/rows/duration; files retained 30 days (R24).

### F31. Admin sends approved records to an external API
1. Project → Output Destinations → **New API Connector**: URL, method, headers, auth (none/API key/bearer/basic), body template with `{{…}}` placeholders, field mapping, single vs batch mode (+ batch size), success codes, response ID JSONPath, timeout, retry policy, create-or-update behavior.
2. **Test Request** (dry run): renders payload for a sample approved record, optionally sends to the endpoint, shows request+response. **Payload Preview** without sending.
3. **New Delivery**: scope (default: ReadyForExport, not yet delivered by this connector), preview count → `DeliveryRun` + per-record `api_deliveries` (`Pending`) → `ApiDeliveryJob` sends with idempotency keys (D15); per-record status live-updates. Success → record `Exported` (delivery status), external ID stored.

### F32. Failed API deliveries are retried
1. Automatic: retry policy (default 3 attempts, exponential backoff 30s/2m/10m) on 5xx/timeout/429; 4xx (except 408/429) are permanent → `Failed`.
2. Manual: Delivery Run detail → **Retry Failed** re-queues `Failed` rows (attempt history preserved). Exhausted retries → `DeadLettered`, visible + notifiable to admins.

### F33. Admin reviews Audit Log and usage reports
1. Audit Log (global or project): filter by actor, category, entity, date, correlation ID; each row expands to details (before/after summary); export CSV.
2. Usage & Costs: token/cost totals by project/provider/model/day, source split (playground/processing/test), budget consumption bars, cost per approved record, model price table editor (`system.providers.manage`).

### F-link. Telegram account linking (referenced by F18/F19)
1. User (any role) → Settings → Telegram Linking → **Generate Code**: 8-char one-time code (10-min TTL, hashed at rest, rate-limited 5/hour).
2. User opens the bot → `/start <code>` (or taps the deep link `t.me/<bot>?start=<code>` shown as a button/QR).
3. Bot validates: code exists, unexpired, unused → binds `telegram_user_id` ↔ user, marks code used, replies "Account linked ✅", notifies in-app, audits `telegram.linked`.
4. Unlink: from app Settings (or admin revoke in User Management). Bot access immediately invalid; Mini App auth fails for that Telegram ID. Takeover prevention: linking a Telegram ID already linked to another user is rejected with guidance; codes are single-use and expire.
