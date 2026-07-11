# 11 — API Design

## Conventions (binding)

- Base path `/api/v1`. JSON camelCase. OpenAPI document generated and committed check-in (`/openapi/v1.json`); TS types generated from it at frontend build.
- **Auth:** `Authorization: Bearer <access JWT>` (15 min) + refresh token rotation (`POST /auth/refresh`, refresh JWT 14 days, stored hashed, rotated on use, reuse detection revokes family). No cookies for the API ⇒ CSRF not applicable; tokens kept in memory + refresh in `localStorage` (documented tradeoff; XSS mitigations in doc 16).
- **Errors:** RFC 7807 `application/problem+json`: `{type, title, status, code, detail?, errors?: {field: [messages]}, traceId}`. `code` is a stable machine string (see [17-error-handling-matrix.md](17-error-handling-matrix.md)). Validation failures → 400 with `errors`; permission → 403 `permission_denied`; missing → 404 `not_found`; state conflicts → 409 (`invalid_state`, `version_conflict`, `duplicate`); lock → 423 `record_locked`; rate limit → 429.
- **Concurrency:** mutating endpoints on versioned entities require `version` in the body; mismatch → 409 `version_conflict` + current server representation in `extensions.current`.
- **Idempotency:** run-starting POSTs (`processing-runs`, `imports/{id}/start`, `export-runs`, `delivery-runs`, bulk assignment) accept `Idempotency-Key` header; same key + same body within 24 h returns the original result (`idempotency_keys` handled via unique constraint on audit of key hash → returns stored response envelope).
- **Pagination:** list responses `{items: [], nextCursor?: string, totalCount?: number}` (totalCount only where cheap). Filters as query params.
- **Project scoping:** everything project-bound lives under `/projects/{projectId}/…` and passes `ProjectAccessFilter`.
- **Correlation:** `X-Correlation-Id` in/out on every request.
- Secrets: never echoed; masked strings (`••••1234`); "replace" semantics on update (absent = keep).

## Endpoint catalog

Legend: 🔑 = permission. Standard CRUD contracts are implied (GET returns DTO; POST creates → 201 + body; PUT full-update with `version`; DELETE → 204).

### Auth & self
| Method | Route | Purpose | 🔑 |
|---|---|---|---|
| POST | `/auth/login` | email+password → tokens (+`mustChangePassword`) | anon (rate-limited 5/min/IP) |
| POST | `/auth/refresh` | rotate refresh → new pair | anon+token |
| POST | `/auth/logout` | revoke refresh family | authenticated |
| POST | `/auth/change-password` | change own password | authenticated |
| POST | `/auth/telegram-miniapp` | Telegram `initData` → tokens | anon (validated) |
| GET | `/me` | profile, permissions, project memberships | authenticated |
| GET/PUT | `/me/settings` | theme, notification prefs | authenticated |

### Users, roles (global)
| Method | Route | 🔑 |
|---|---|---|
| GET/POST | `/users` · GET/PUT `/users/{id}` | `system.users.manage` (GET list also Auditor) |
| POST | `/users/{id}/set-password` · `/users/{id}/deactivate` · `/users/{id}/reactivate` | `system.users.manage` |
| POST | `/users/{id}/revoke-telegram` | `system.users.manage` |
| GET/POST | `/roles` · GET/PUT/DELETE `/roles/{id}` | `system.roles.manage` |
| GET | `/permissions` (catalog of constants) | `system.roles.manage` |

### AI providers & prices (global)
| Method | Route | 🔑 |
|---|---|---|
| GET/POST | `/providers` · GET/PUT/DELETE `/providers/{id}` | `system.providers.manage` (GET list minimal DTO for `project.ai.manage` holders) |
| POST | `/providers/{id}/test` | test connection (optionally with unsaved body) |
| POST | `/providers/{id}/fetch-models` | OpenRouter model list sync |
| GET/PUT | `/model-prices` | price grid; PUT upserts rows |

### Projects & members
| Method | Route | 🔑 |
|---|---|---|
| GET | `/projects` (memberships-filtered) | authenticated |
| POST | `/projects` | `projects.create` |
| GET/PUT | `/projects/{id}` | `project.view` / `project.settings.manage` |
| PUT | `/projects/{id}/wizard-state` | save wizard progress | 
| POST | `/projects/{id}/archive` · `/unarchive` | `project.settings.manage` |
| GET/POST/DELETE | `/projects/{id}/members` (+PUT role) | `project.members.manage` |
| GET | `/projects/{id}/overview` (funnel counts, checklist, active runs, budget) | `project.view` |

### Schema & prompt versions (identical shape; `prompt` mirrors `schema`)
| Method | Route | 🔑 |
|---|---|---|
| GET | `/projects/{id}/schema/versions` · `/{versionId}` | `project.view` |
| GET/PUT | `/projects/{id}/schema/draft` (auto-creates draft from latest published) | `project.schema.manage` |
| POST | `/projects/{id}/schema/draft/publish` `{changeNote}` | `project.schema.manage` |
| GET | `/projects/{id}/schema/versions/{a}/diff/{b}` | `project.view` |
| POST | `/projects/{id}/schema/versions/{versionId}/restore` (→ new draft) | `project.schema.manage` |

### Playground & test cases
| Method | Route | 🔑 |
|---|---|---|
| POST | `/projects/{id}/playground/run` | `project.playground.use` |
| GET/POST | `/projects/{id}/test-cases` · GET/PUT/DELETE `/{tcId}` | `project.playground.use` |
| POST | `/projects/{id}/test-cases/{tcId}/run` · `/test-cases/run-all` | `project.playground.use` |

### Ingestion
| Method | Route | 🔑 |
|---|---|---|
| POST | `/projects/{id}/imports/upload` (multipart) → run in `Uploaded`, returns preview | `project.imports.run` |
| GET | `/projects/{id}/imports` · `/{runId}` (+ live counters) | `project.imports.run` |
| POST | `/projects/{id}/imports/{runId}/mapping` (validate+preview warnings) | `project.imports.run` |
| POST | `/projects/{id}/imports/{runId}/start` ⏯idempotent | `project.imports.run` |
| POST | `/projects/{id}/imports/{runId}/cancel` | `project.imports.run` |
| GET | `/projects/{id}/imports/{runId}/row-errors.csv` | `project.imports.run` |
| POST | `/projects/{id}/records/manual` (single) · `/manual-bulk` (paste parse+preview then commit) | `project.records.manage` |
| GET/POST | `/projects/{id}/input-connectors` · GET/PUT/DELETE `/{cId}` | `project.connectors.manage` |
| POST | `/projects/{id}/input-connectors/{cId}/test` · `/preview` · `/run` ⏯ · `/enable` · `/disable` | `project.connectors.manage` |
| GET | `/projects/{id}/input-connectors/{cId}/sync-runs` | `project.connectors.manage` |

### Records
| Method | Route | 🔑 |
|---|---|---|
| GET | `/projects/{id}/records` (filters: q, externalId, processingStatus[], reviewStatus[], deliveryStatus[], reviewerId, importRunId, confidenceBand, hasValidationIssues, priority, dateFrom/To, field containment `fieldFilter={json}`; cursor) | `project.records.view` (reviewer-scoped) |
| GET | `/projects/{id}/records/{rid}` (+`?include=extractions,review,deliveries,events`) | `project.records.view` |
| DELETE | `/projects/{id}/records/{rid}` (only Imported, R22) + bulk variant | `project.records.manage` |
| POST | `/projects/{id}/records/{rid}/release-lock` | `project.records.manage` |
| GET | `/projects/{id}/records/{rid}/extractions` · `/{extractionId}` (raw response included only for admins) | `project.records.view` |

### Processing
| Method | Route | 🔑 |
|---|---|---|
| POST | `/projects/{id}/processing-runs/estimate` (scope → count, tokens, cost) | `project.processing.run` |
| POST | `/projects/{id}/processing-runs` ⏯ | `project.processing.run` |
| GET | `/projects/{id}/processing-runs` · `/{runId}` (+counters) · `/{runId}/tasks` (filter/cursor) | `project.view` |
| POST | `/{runId}/pause` · `/resume` · `/cancel` · `/retry-failed` ⏯ | `project.processing.control` |

### Reviews & assignments
| Method | Route | 🔑 |
|---|---|---|
| POST | `/projects/{id}/assignment-batches/preview` (scope+strategy → distribution) | `project.assignments.manage` |
| POST | `/projects/{id}/assignment-batches` ⏯ | `project.assignments.manage` |
| GET | `/projects/{id}/assignment-batches` · `/{batchId}` · `/assignments` (filters) | `project.assignments.manage` |
| POST | `/assignments/bulk-action` `{action: reassign|unassign|cancel|changePriority|changeDueDate|releaseLocks, assignmentIds|filter, params}` → per-row results | `project.assignments.manage` |
| GET | `/review/tasks` (cross-project summary for current reviewer) | `project.reviews.own` |
| GET | `/projects/{id}/review/queue` (own assigned; filters; cursor) | `project.reviews.own` |
| GET | `/projects/{id}/review/queue/next?after={recordId}` | `project.reviews.own` |
| POST | `/projects/{id}/records/{rid}/lock` · `/lock/heartbeat` · `/lock/release` | `project.reviews.own` |
| PUT | `/projects/{id}/records/{rid}/review/draft` `{draftOutput, baseVersion, lockToken}` | `project.reviews.own` |
| POST | `/projects/{id}/records/{rid}/review/approve` `{finalOutput, baseVersion, lockToken}` | `project.reviews.own` |
| POST | `/projects/{id}/records/{rid}/review/reject` `{note, …}` · `/return` `{note, …}` · `/skip` | `project.reviews.own` |
| POST | `/projects/{id}/review/bulk` `{action: approve|reject|return, recordIds, note?}` → per-record results | `project.reviews.own` (+`project.reviews.bulk_approve` for approve) |
| GET | `/review/completed` · `/review/progress` | `project.reviews.own` |

### Delivery
| Method | Route | 🔑 |
|---|---|---|
| POST | `/projects/{id}/export-runs` ⏯ `{scope, columnConfig, format}` | `project.exports.run` |
| GET | `/projects/{id}/export-runs` · `/{runId}` · GET `/{runId}/download` (file stream) · POST `/{runId}/cancel` | `project.exports.run` |
| GET/PUT | `/projects/{id}/export-template` (saved default column config) | `project.exports.run` |
| GET/POST | `/projects/{id}/output-connectors` · GET/PUT/DELETE `/{cId}` · POST `/test` (dryRun flag) | `project.connectors.manage` |
| POST | `/projects/{id}/delivery-runs` ⏯ `{outputConnectorId, scope, redeliver?}` | `project.deliveries.run` |
| GET | `/projects/{id}/delivery-runs` · `/{runId}` · `/{runId}/deliveries` (filter/cursor) | `project.deliveries.run` |
| POST | `/{runId}/retry-failed` ⏯ · `/deliveries/{dId}/retry` | `project.deliveries.run` |

### Telegram, notifications, dashboards, audit, usage, settings
| Method | Route | 🔑 |
|---|---|---|
| POST | `/telegram/link-codes` (generate own code) · DELETE `/telegram/link` (unlink self) | authenticated |
| GET | `/telegram/link` (own status) | authenticated |
| POST | `/telegram/webhook/{secret}` (bot updates; Telegram secret-token header verified) | anon (guarded) |
| GET | `/notifications` · POST `/notifications/mark-read` `{ids|all}` | authenticated |
| GET | `/dashboard` (global metrics) · `/projects/{id}/dashboard` | `project.view` |
| GET | `/review-ops` · `/projects/{id}/review-ops` (backlog + reviewer table) | `project.assignments.manage` |
| GET | `/audit` · `/projects/{id}/audit` (filters, cursor, CSV export param) | `system.audit.view` / `project.audit.view` |
| GET | `/usage` · `/projects/{id}/usage` (aggregates by day/model/provider/source) | `system.usage.view` / `project.usage.view` |
| GET/PUT | `/settings` (global app settings; Telegram bot config; PUT sections) | `system.settings.manage` |
| POST | `/settings/telegram/set-webhook` · `/settings/telegram/test` | `system.settings.manage` |
| GET | `/health` (liveness) · `/health/ready` (db, hangfire, storage, optional provider ping) | anon / anon |

### SignalR hub `/hubs/events` (JWT auth)
Groups: `project:{id}` (join requires `project.view`), `user:{id}` (auto). Server→client messages: `ImportProgress`, `ProcessingProgress {runId, counters, cost}`, `TaskFailed`, `ExportProgress`, `DeliveryProgress`, `ReviewQueueChanged`, `AssignmentCreated`, `RecordLockChanged`, `NotificationReceived`. Client falls back to 10 s polling of the matching GET endpoints when disconnected.

## Detailed contracts (most complex endpoints)

### POST `/projects/{id}/playground/run`
Req: `{inputText, contextValues?, aiProviderId?, model?, schemaVersionId?, promptVersionId?, generationSettings?}` (nulls = project defaults). Validation: text ≤ 100 KB; versions belong to project. Resp 200: `{compiledPrompt: {system, user}, rawResponse, parsedOutput?, fieldMeta?, validationResult?, usage: {inputTokens, outputTokens, estimatedCost}, durationMs, error?: {code, message, repairAttempts?}}`. Errors: 400 validation; 409 `provider_disabled`; 502 `provider_unavailable`; 504 `provider_timeout`. Not idempotent (each call costs money) — UI guards double-submit.

### POST `/projects/{id}/processing-runs`
Req: `{scope: {mode: "selected|nextN|allUnprocessed|all|filter|retryFailed|reprocessRejected|reprocessLowConfidence|needsReprocessing", recordIds?, n?, filter?, includeApproved?: false, confidenceBelow?}, model?, aiProviderId?, generationSettings?, concurrency?, budgetLimit?, errorRateThreshold?, pauseAfterN?, confirmationPhrase?}`.
Validation: published schema+prompt exist; provider enabled; scope resolves ≥1 record; estimated cost > confirm threshold ⇒ `confirmationPhrase == project.name`; `includeApproved` ⇒ additionally requires `project.settings.manage`. Resp 201: run DTO. Side effects: snapshot, tasks created, records → Queued (state guards skip ineligible with per-record report in `extensions.skipped`). Idempotent via `Idempotency-Key`.

### PUT `/projects/{id}/records/{rid}/review/draft`
Req: `{draftOutput, baseVersion, lockToken}`. Server: assignment ownership → lock token valid → `records.version == baseVersion` → normalize+validate draft (warnings allowed, errors allowed in draft) → save; returns `{version, validationResult, savedAt}`. Errors: 403 not assignee; 423 `record_locked`; 409 `version_conflict` (+current server draft/output); 409 `invalid_state` unless status ∈ Assigned|InReview|DraftSaved.

### POST `/projects/{id}/records/{rid}/review/approve`
Req: `{finalOutput, baseVersion, lockToken}`. Server pipeline (one transaction): ownership+lock+version → full validation (**errors block**, `required` enforced) → compute `fieldChanges` diff vs base extraction → write review row, record (`Approved`, `ReadyForExport`, `final_output`), assignment `Completed`, release lock, audit, review event, SignalR. Resp: `{nextRecordId?}` (server picks next queue item). Errors as draft + 422 `validation_errors` with per-field list.

### POST `/projects/{id}/review/bulk`
Req: `{action, recordIds (≤200), note?}`. Resp 200: `{results: [{recordId, ok, code?}]}` — always 200; per-record outcomes. Approve applies R20 safeguards per record; each record processed in its own transaction.

### POST `/projects/{id}/imports/{runId}/mapping`
Req: `{recordIdColumn?, textColumn, metadataColumns[], contextColumns[], duplicatePolicy, sheetName?, hasHeaderRow, encoding?}`. Resp: `{warnings: {duplicateIds, emptyIds, emptyText, oversizedText, invalidRows}, sampleMappedRecords[≤10]}`. Moves run to `AwaitingMapping` with stored mapping.

### POST `/projects/{id}/output-connectors/{cId}/test`
Req: `{recordId?, dryRun: true}` — picks sample approved record (or given), renders payload. `dryRun:false` actually sends. Resp: `{renderedPayload, sent, statusCode?, responseExcerpt?, extractedExternalId?, error?}`. SSRF vetting runs before any send.
