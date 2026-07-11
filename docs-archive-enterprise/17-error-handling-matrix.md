# 17 — Error Handling Matrix

Columns: stable `code` (problem+json / task error code), detection point, system behavior, user experience, recovery path.

| # | Scenario | Code | Detection | System behavior | UX | Recovery |
|---|---|---|---|---|---|---|
| E1 | Invalid Excel file (corrupt, wrong type, bad encoding) | `import_invalid_file` | upload / preview parse | Run → `Failed` (or upload rejected 400); nothing imported | Banner with cause ("File is not a valid .xlsx") + accepted-formats hint | Re-upload corrected file |
| E2 | Duplicate record ID in file or project | `import_duplicate_id` | mapping preview + import row loop | Policy `Skip`: counted+skipped, row error logged; `FailRow`: row error | Warning counts in mapping; row-errors CSV lists each | Download errors, fix source, re-import (dedup makes re-import safe) |
| E3 | Empty ID / empty text row | `import_empty_id` / `import_empty_text` | same | Row skipped + row error (or ID generated if "generate IDs" chosen) | Same as E2 | Same |
| E4 | Oversized cell / row | `import_row_too_large` | streaming parser guard | Row error, import continues | Warning + CSV | Trim source |
| E5 | Provider unavailable (conn refused/DNS/5xx) | `provider_unavailable` | extraction HTTP call | Retry ×max_retries (backoff+jitter); then task `Failed`; run continues; error-rate stop if threshold hit | Run detail error breakdown; provider health strip red | Retry Failed run once provider recovers |
| E6 | Provider rate limit (429) | `provider_rate_limited` | HTTP 429 | Respect `Retry-After`, adaptive RateGate slowdown; retries don't count as failures until budget of retries exhausted | Throughput dip visible; log note | Automatic |
| E7 | Provider timeout | `provider_timeout` | HttpClient timeout | Same retry path as E5 | Same | Same; consider raising provider timeout |
| E8 | Invalid JSON from model | `invalid_json` | parse stage | Deterministic repair → one re-ask → task `ParseFailed`/`Failed`; raw stored | Error breakdown; record filter `ProcessingFailed` | Retry failed / different model scope |
| E9 | Schema validation failure of AI output | `validation_failed` | validate stage | `extraction_results.status=ValidationFailed`; record `ValidationFailed`; issues stored | Record badge amber; issues listed in detail/reviewer view | Reprocess (better prompt/model) or manual review-fix if assigned anyway |
| E10 | Budget exceeded during run | `budget_exceeded` | pre-dispatch + post-response checks | Run `StoppedByBudget`; queued tasks `Cancelled`; in-flight complete | Banner on run + notification with spent/limit | Raise budget or new run for remainder |
| E11 | Output API authentication failure (401/403) | `delivery_auth_failed` | delivery response | Permanent → delivery `Failed` (no retry burn) | Delivery run shows failure reason | Fix connector credentials, Retry Failed |
| E12 | Output API timeout / 5xx | `delivery_timeout` / `delivery_unavailable` | delivery HTTP | Retry per policy → `Failed` → manual retry → `DeadLettered` | Statuses per record; dead-letter banner + admin notification | Retry after endpoint recovers |
| E13 | Duplicate submission risk (retry after unknown outcome) | `delivery_unknown_outcome` | timeout after send | Same `Idempotency-Key` reused on retry; Delivered-unique constraint prevents local dupes | Attempt log shows retries | Receiver-side dedupe via key; documented |
| E14 | Telegram link code expired/used/invalid | `telegram_code_invalid` | `/start <code>` | Bot explains and points to regenerate | Bot message; app shows link still pending | Generate new code |
| E15 | Network disconnected (reviewer PWA) | — (client) | fetch failure / offline event | Drafts → IndexedDB; queue UI reads cache; decisions disabled | Offline banner + per-record pending-sync badge | Auto-sync on reconnect (D17) |
| E16 | Concurrent edit conflict | `version_conflict` | optimistic check on save | 409 + server state returned; nothing overwritten | Conflict panel: local vs server per field, choose/merge | Reviewer resolves, resaves |
| E17 | Record locked by another session | `record_locked` | lock acquire | 423 with holder info + expiry | Read-only banner "Locked by X until…" | Wait/expiry; admin Release Lock |
| E18 | Application restart mid-everything | — | startup recovery | Hangfire re-runs; JobRecoveryService requeues heartbeat-stale tasks; SignalR clients reconnect + reconcile via GET | Brief reconnect toast; progress resumes | Automatic (D14) |
| E19 | Queue (Hangfire/DB) unavailable | `jobs_unavailable` | health check + enqueue failure | Enqueue endpoints 503; readiness fails; existing state intact | "Background processing unavailable" banner from health poll | Restore DB; jobs resume |
| E20 | Schema changed during processing | — | prevented by design | Runs pin schema_version_id (D8); publish creates new version, never mutates | Info note on publish when runs active | n/a |
| E21 | Prompt changed during processing | — | same as E20 via prompt version pinning | Same | Same | n/a |
| E22 | Reviewer edits during reprocessing of same record | `invalid_state` | reprocess scope guard + review write guard | Records `InReview/DraftSaved` excluded from scopes by default; if forced, record locked-for-review wins: task skips with `skipped_in_review` | Skip counts in run report | Re-run scope later |
| E23 | Approved record almost reprocessed accidentally | `approved_excluded` | scope resolution | Excluded by default; explicit override path (R13) with confirmation + audit | Dialog shows excluded count + override consequences | Intentional override only |
| E24 | Assignment conflict (two admins assign same records) | `assignment_conflict` | partial unique Active index | Second insert skipped per record; per-row result reports "already assigned" | Batch result summary lists conflicts | Re-run scope for remainder |
| E25 | Job processed twice (Hangfire redelivery) | — | unique constraints + status guards | Second execution no-ops ("already terminal") | Invisible | n/a; duplicate provider cost possible and logged (T10) |
| E26 | Cost limit hit mid-run vs in-flight requests | `budget_exceeded` | post-response check | In-flight complete and are recorded; overshoot ≤ concurrency × per-record cost, documented | Run banner explains small overshoot | n/a |
| E27 | Excel export interrupted | — | job crash/cancel | Temp file deleted; run `Failed`/`Cancelled`; no partial download exposed | Run status + retry button (new run) | Re-run export |
| E28 | Export file expired | `export_expired` | download route | 410 Gone | "File expired — run export again" | Re-run |
| E29 | Schema version conflict (draft edited concurrently by two admins) | `version_conflict` | optimistic version on draft PUT | 409 + latest draft returned | Conflict dialog (reload/overwrite explicit) | Manual merge |
| E30 | Lost offline edits (cache cleared before sync) | — (client) | sync engine detects missing baseline | Warned at logout with unsynced drafts (blocked/confirm); IndexedDB persisted across sessions | Warning dialogs | Prevented unless user forces discard |
| E31 | Playground/provider misconfig (no published schema/prompt, provider disabled) | `configuration_incomplete` / `provider_disabled` | precondition checks | 409 with which prerequisite is missing | Setup checklist deep link | Complete configuration |
| E32 | Invalid connector JSONPath / template placeholder at runtime | `mapping_error` | sync/delivery render | Row/delivery error, run continues where possible | Error rows in sync history / delivery attempts | Fix config, re-run |

General rules: user-facing messages are English, actionable, never expose stack traces or secrets; every 5xx returns problem+json with `traceId` correlating to logs; all background failures are observable in their run/task tables — **no silent failures**; toasts are used only for transient confirmations, never as the sole record of an error.
