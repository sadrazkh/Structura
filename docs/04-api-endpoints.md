# 04 — API Endpoints

## Conventions

- Base `/api`; Bearer JWT (access 15 min + rotating refresh 14 d, hashed in DB). problem+json errors with stable `code` (`invalid_credentials, permission_denied, not_found, invalid_state, version_conflict, duplicate, validation_failed, provider_error, record_locked→(not used), setup_required`). Validation errors → 400 with `errors: {field: [...]}`.
- Project routes require membership (or Administrator). Reviewer record queries are always filtered `assigned_reviewer_id = currentUser` server-side.
- Lists: `{items, nextCursor?}`; records use keyset on `(updated_at, id)`.
- Mutations on records carry `version`; mismatch → 409 `version_conflict` + current state.

## Auth & users

| Method & route | Purpose | Access |
|---|---|---|
| POST `/auth/login` | tokens + mustChangePassword flag | anon (rate-limited) |
| POST `/auth/refresh` · `/auth/logout` | rotate / revoke | token |
| POST `/auth/change-password` | own password | any |
| POST `/auth/telegram-miniapp` | Telegram initData → tokens | anon (HMAC-validated) |
| GET `/me` | profile, role, memberships | any |
| GET/POST `/users` · PUT `/users/{id}` | manage users | Administrator |
| POST `/users/{id}/reset-password` · `/deactivate` · `/reactivate` | | Administrator |

## Projects & configuration

| Method & route | Purpose | Access |
|---|---|---|
| GET/POST `/projects` | list (membership-filtered) / create | any / Admin+PM |
| GET/PUT `/projects/{id}` | detail / update name+description | member |
| POST `/projects/{id}/archive` | archive | Admin+PM member |
| GET/POST/DELETE `/projects/{id}/members` | manage members | Admin+PM member |
| GET/PUT `/projects/{id}/schema` | fields JSON (PUT validates, bumps schema_version) | Admin+PM member |
| GET/PUT `/projects/{id}/ai-config` | provider config (key masked/replace-only) + prompt config | Admin+PM member |
| POST `/projects/{id}/ai-config/test` | live test call to provider | Admin+PM member |
| GET `/projects/{id}/dashboard` | status counters, active run, reviewer progress | member |

## Import

| Method & route | Purpose |
|---|---|
| POST `/projects/{id}/imports/upload` | multipart xlsx/csv → run `{id, previewRows[20], columns[]}` |
| POST `/projects/{id}/imports/{runId}/start` | body: `{idColumn?, textColumn}` → background import |
| POST `/projects/{id}/imports/{runId}/cancel` · GET `/imports` · GET `/imports/{runId}` | control + progress |
| POST `/projects/{id}/records/manual` | `{externalId?, text}` or `{bulkText}` (line/TSV parse, preview+commit) |
| PUT `/projects/{id}/api-input` | save API input config |
| POST `/projects/{id}/api-input/test` | fetch + preview mapped rows (no insert) |
| POST `/projects/{id}/api-input/fetch` | fetch + insert (dedup by external_id) → import run summary |

All Admin+PM member.

## Records & processing

| Method & route | Purpose |
|---|---|
| GET `/projects/{id}/records` | filters: `q, processingStatus, reviewStatus, deliveryStatus, reviewerId, cursor` |
| GET `/projects/{id}/records/{rid}` | detail incl. latest extraction + attempts |
| DELETE `/projects/{id}/records` | `{recordIds}` — only Pending+Unassigned |
| POST `/projects/{id}/runs` | `{scope: "selected"\|"allPending"\|"failed"\|"reprocessRequested", recordIds?}` → run |
| GET `/projects/{id}/runs` · `/runs/{runId}` | list / detail (live counters, errors, tokens) |
| POST `/projects/{id}/runs/{runId}/cancel` | cooperative cancel |
| POST `/projects/{id}/runs/{runId}/retry-failed` | new run over failed records of that run |

## Assignment & review

| Method & route | Purpose | Access |
|---|---|---|
| POST `/projects/{id}/assignments` | `{recordIds, mode: "single"\|"distribute", reviewerId?\|reviewerIds}` → per-record result | Admin+PM |
| POST `/projects/{id}/assignments/unassign` · `/reassign` | `{recordIds}` / `{recordIds, reviewerId}` | Admin+PM |
| GET `/projects/{id}/review-status` | counters + per-reviewer stats | Admin+PM |
| GET `/review/tasks` | reviewer's cross-project pending summary | Reviewer |
| GET `/review/{projectId}/records` | own assigned list (filter/cursor) | Reviewer |
| GET `/review/{projectId}/records/{rid}` | opens record (sets InReview) + schema snapshot for form | Reviewer(owner) |
| GET `/review/{projectId}/next?after={rid}` | next assigned record id | Reviewer |
| PUT `/review/{projectId}/records/{rid}` | save edits `{finalOutput, version}` | Reviewer(owner) |
| POST `…/{rid}/approve` | `{finalOutput, version}` — validates required+allowedValues+types | Reviewer(owner) |
| POST `…/{rid}/reject` · `/reprocess` | `{note, version}` | Reviewer(owner) |
| POST `/review/{projectId}/bulk-approve` | `{recordIds}` → per-record results (re-validated server-side) | Reviewer |
| GET `/review/progress` | own stats | Reviewer |

## Export & delivery

| Method & route | Purpose |
|---|---|
| GET `/projects/{id}/export/excel?from=&to=` | **streaming** xlsx of Approved records (sanitized) |
| GET/PUT `/projects/{id}/api-output` | output config (secrets masked) |
| POST `/projects/{id}/api-output/test` | render body for a sample approved record; optional real send |
| POST `/projects/{id}/deliveries/start` | queue all Approved+Pending records for delivery |
| GET `/projects/{id}/deliveries` | per-record delivery table (status, attempts, error) |
| POST `/projects/{id}/deliveries/retry-failed` | reset Failed → Pending, re-queue |

## Telegram & settings

| Method & route | Purpose | Access |
|---|---|---|
| POST `/telegram/link-code` | generate own linking code | any |
| GET `/telegram/link` · DELETE `/telegram/link` | status / unlink | any |
| POST `/telegram/webhook/{secret}` | bot updates | anon (secret + header check) |
| GET/PUT `/settings` | bot token (masked), mode, public base URL | Administrator |
| POST `/settings/telegram/set-webhook` · `/test` | webhook mgmt / getMe | Administrator |
| GET `/health` | liveness + DB check | anon |

## SignalR hub `/hubs/progress` (JWT)

Groups per project (membership-checked on join). Messages: `ImportProgress {runId, imported, failed}`, `RunProgress {runId, succeeded, failed, total}`, `DeliveryProgress {delivered, failed, total}`. Clients poll the matching GET every 5 s when the socket is down.
