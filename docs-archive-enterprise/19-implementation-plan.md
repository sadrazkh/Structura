# 19 — Implementation Plan (Epics → Features → Tasks)

## Global Definition of Done (applies to every task; per-task DoD lists only additions)

1. Code compiles; migrations apply cleanly to an empty DB and to the previous state.
2. Backend permission enforced + FluentValidation on requests; state machines respected.
3. Audit events written for state changes; correlation ID flows.
4. Unit tests for domain logic + integration test for the endpoint/job happy path **and** the listed edge cases; all tests green.
5. Frontend (when in scope): loading/empty/error states, mobile behavior per doc 06, English UI, keyboard accessible.
6. No TODO/placeholder/mock left; OpenAPI updated; no secret in code.

Task IDs are stable references for the build order (doc 20). "⚠" marks tasks with heavy edge-case load.

---

## E0 — Solution Scaffold

**T0.1 Solution & module skeleton**
- Goal: compilable skeleton of the structure in doc 02.
- Scope: solution, all projects, `ModuleSetup` pattern, Serilog, problem+json middleware, correlation middleware, health endpoints, OpenAPI generation, Vite SPA scaffold (Vue3+TS+Tailwind+router+pinia+query), SPA dev proxy + production static serving, `.editorconfig`, CI script (`build + test + lint`).
- Deps: —. DB: none. Security: security headers middleware.
- Acceptance: `dotnet test` green; `/health` 200; SPA "hello" served by host in prod build; problem+json returned for a thrown test exception.
- Edge cases: SPA fallback route must not swallow `/api`, `/hubs`, `/hangfire`.
- Tests: middleware unit tests; smoke integration test.

**T0.2 Persistence foundation**
- Goal: `AppDbContext` + conventions + Testcontainers harness.
- Scope: snake_case naming convention, UUIDv7 generator, timestamps interceptor, string-enum converters, `version` concurrency convention, migration-at-startup with advisory lock, `citext` extension, seeding pipeline skeleton.
- Acceptance: empty migration applies; two app instances starting concurrently migrate exactly once (advisory-lock test).
- Edge cases: migration failure → app exits non-zero with clear log.

**T0.3 SharedKernel primitives**
- Goal: `Result`, `ErrorCodes` (doc 17 catalog), `ICurrentUser`, `IClock`, `ISecretProtector` (DataProtection impl + `protected:v1:` format), pagination primitives (keyset cursor codec), `IAuditWriter` contract, guard clauses.
- Acceptance: secret round-trip + masking helper (`••••1234`) unit-tested; cursor codec property-tested.

## E1 — Identity & Access

**T1.1 Users, roles, permissions (domain + seed)**
- Scope: tables per doc 10 identity, permission constants (doc 03), seeded roles, bootstrap admin from env (F1), last-admin guard.
- Edge cases: bootstrap idempotent across restarts; missing env + no admin → `setup_required` mode.
- Tests: seeder idempotency; last-admin guard.

**T1.2 JWT auth + refresh rotation ⚠**
- Scope: login, refresh (rotation + reuse-detection family revoke), logout, change-password (+ security stamp invalidation), `must_change_password` gate middleware, login rate limit + lockout.
- Acceptance: reuse of rotated refresh revokes family (integration test); expired access → 401 → refresh flow works.
- Edge cases: clock skew ±2 min; concurrent refresh (single winner, loser gets family-safe retry); deactivated user token rejection via stamp.

**T1.3 Authorization pipeline**
- Scope: permission attribute/filter, `ProjectAccessFilter`, `/me` endpoint (permissions + memberships), 403 problem responses.
- Tests: **generated authz matrix test** — every endpoint annotated; test asserts 401/403 per role from doc 03 table (this test grows with every later epic and is the project's authz backbone).

**T1.4 Users & Roles management (API + UI)**
- Scope: endpoints per doc 11; Users + Roles pages per doc 06; set-password, deactivate/reactivate, revoke-telegram (stub until E16).
- Edge cases: email uniqueness (citext), self-deactivation block, role delete blocked while assigned.

**T1.5 Sign-in + app shell (UI)**
- Scope: Sign In + forced Change Password pages, auth store (token memory + refresh persistence), route guards, AdminLayout + ReviewerLayout shells, theme toggle (light/dark/system), design-system foundation components (tokens + Button/Input/Dialog/Toast/Table/EmptyState/Skeleton/Badge/ConfirmDialog).
- Acceptance: full login→dashboard→refresh-after-expiry→logout cycle in Playwright.

## E2 — Projects

**T2.1 Project CRUD + members**
- Scope: project entity (+settings JSONBs with defaults), members endpoints/UI, archive, overview endpoint skeleton (counts stubbed to zero until Records), Projects list + Members page.
- Edge cases: name uniqueness; archived projects read-only (write endpoints 409 `project_archived`); creator auto-membership.

**T2.2 Create Project Wizard**
- Scope: wizard UI (5 steps, R10), `wizard_state` persistence, Draft→Active on completion, auto-publish schema v1 + prompt v1 at creation.
- Deps: T3.x, T4.x, T5.x (embedded builders) — wizard shell built here with placeholder steps wired as those epics land; final integration task T2.3.
- **T2.3 Wizard integration** (after E3/E4/E5): embed real builders, sample-test step via Playground endpoint; resume-from-draft tested.

## E3 — Schema

**T3.1 FieldSpec model + definition validation ⚠**
- Scope: C# `FieldSpec` tree, definition JSON (de)serialization, definition validator (keys, regex validity, depth/count caps, allowedValues consistency, dependsOn resolution, export modes), Person/Location templates.
- Tests: exhaustive validator unit tests incl. every field type, nesting, broken regex, duplicate keys at different levels (legal) vs same level (illegal).

**T3.2 Schema version lifecycle**
- Scope: draft get-or-create, draft PUT (optimistic), publish (sequential numbering, immutability interceptor, fresh draft copy), versions list, diff (structural: added/removed/changed with property-level detail), restore-as-draft.
- Edge cases: publish with zero fields → 400; concurrent draft edit → 409; diff across non-adjacent versions.

**T3.3 Schema Builder UI ⚠**
- Scope: three-pane builder per doc 06/W3: tree with drag-reorder + nesting, field editor (all FieldSpec props, grouped), live JSON shape + form preview (uses E10 renderer read-only mode), publish dialog with diff, versions page.
- Edge cases: reordering across nesting levels; deleting a field referenced by dependsOn (blocked with message); unsaved-changes guard.

## E4 — AI Providers

**T4.1 Provider entity + secret handling**
- Scope: CRUD endpoints (masking, replace-only key), enable/disable, delete-blocked-when-referenced, `ProviderConfig` resolution (decrypt at use, never in DTOs), proxy config, model prices table + endpoints.
- Tests: masking; snapshot-never-contains-key property test.

**T4.2 OpenAiCompatible adapter + RateGate ⚠**
- Scope: `IAiCompletionProvider`, adapter with per-type decoration (OpenRouter/NVIDIA/custom), SafeHttp `AiEgress` profile, retry/backoff/jitter + Retry-After, RateGate (concurrency/RPM/TPM), token usage extraction, error classification (doc 17 E5–E8), TestAsync, ListModels (OpenRouter).
- Tests: WireMock suite — 200, 429+Retry-After, 500×N→success, timeout, malformed body, streaming-off content variants; RateGate concurrency property test.

**T4.3 Providers UI**
- Scope: Providers page + editor drawer + test connection UX + Model Prices grid (+ manual "Sync from OpenRouter" using ListModels/pricing when available).

## E5 — Prompts

**T5.1 Prompt version lifecycle** — mirror of T3.2 for `Config` doc (validator: template placeholders, few-shot expected JSON validated against **current draft/published schema** with clear error).
**T5.2 PromptBuilder ⚠** — doc 13 §1: sections, nonce delimiting, few-shot as message pairs, output-language and behavior directives, field-spec rendering; golden-file tests (fa + en samples).
**T5.3 Prompt Configuration UI** — form + live compiled-prompt preview (server-side compile endpoint reused from Playground pipeline in dry mode).

## E6 — Extraction Pipeline Core

**T6.1 JSON Schema generation** — converter per doc 13 §2 with unit tests per field type incl. nested/meta enumeration; identical schema used for validation.
**T6.2 Parser + repair ⚠** — doc 13 §3 pipeline incl. re-ask hook; corpus tests (fences, prose-wrapped, trailing commas, truncated, double-encoded).
**T6.3 SchemaOutputValidator ⚠** — doc 13 §4 with coercions/warnings; **shared test-vector JSON file** consumed by both C# tests and later TS tests (D22).
**T6.4 ExtractionService** — orchestrates build→call→parse→validate→persist for a single input (used by playground/test/processing); usage events; cost calc from prices; behaviors R-mapped.

## E7 — Playground & Test Cases

**T7.1 Playground endpoint + UI** — W5 layout, tabs, metrics; provider/model/version pickers; double-submit guard; per-user concurrency 1.
**T7.2 Test cases** — CRUD + run + run-all + pass/diff computation + UI table/diff views.
- Edge cases: expected output referencing removed fields after schema change → diff marks `field_removed`.

## E8 — Ingestion

**T8.1 Upload + preview + mapping ⚠**
- Scope: multipart upload (validations per doc 16), MiniExcel/CsvHelper preview (50 rows), mapping endpoint with warning computation (dups/empties/oversize via full streaming scan), mapping UI (W6).
- Edge cases: xlsx with formulas (values read), merged cells, empty sheets, BOM/encoding variants, 1-column files, header dedupe.

**T8.2 FileImportJob ⚠**
- Scope: doc 12 §1 exactly; SignalR ImportProgress; cancel; row-errors CSV endpoint; imports history UI + progress screen.
- Tests: 50k-row synthetic file integration test (streaming memory assertion < 300 MB, chunk transactionality, resume-after-kill test).

**T8.3 Manual input** — single + bulk paste (parse preview `ID<TAB>Text` / line mode), UI.

**T8.4 REST input connector ⚠**
- Scope: connector CRUD/test/preview/run/schedule per doc 14; `ConnectorSyncJob` with checkpoint commits; sync history UI; builder UI (W7).
- Tests: WireMock pagination suites (page/cursor/nextUrl), checkpoint resume mid-sync, dedup, SSRF vetting integration (reuses E13 SafeHttp), JSONPath errors as row errors.

## E9 — Records

**T9.1 Record store + filters ⚠**
- Scope: records list endpoint (all filters incl. `fieldFilter` JSONB containment, keyset), detail (+includes), delete guards (R22), release-lock endpoint, Records UI (table, filter bar, drawer preview, bulk selection scaffolding), Record Details UI (tabs; extraction history renders after E10 data exists).
- Tests: filter matrix integration tests; reviewer-scoping test; pagination stability under concurrent updates.

## E10 — Dynamic Form Renderer (frontend core)

**T10.1 Renderer + field components ⚠**
- Scope: registry, recursion, all 18 components, object-list cards, dependsOn visibility, `dir=auto`, default/normalize utilities.
**T10.2 TS validation engine** — interprets FieldSpec rules; consumes the shared test-vector file (T6.3) in Vitest to guarantee C#/TS parity.
**T10.3 Review-mode chrome** — per-field confidence dot, AI-original popover + revert, evidence quote display, modified indicator, validation message slot. Used by Playground preview (read-only), Focus Review, Table drawer.

## E11 — Processing Runs

**T11.1 Run creation + estimation ⚠**
- Scope: estimate endpoint (scope resolution + token/cost heuristics), run creation (snapshot, tasks, state flips, skip-ineligible reporting, confirmation threshold, includeApproved guard R13), scope modes incl. retry/reprocess lineage (`parent_run_id`).
- Edge cases: empty scope 400; records already in another active run → skipped `already_queued`; approved exclusion; idempotency key replay.

**T11.2 Orchestrator + extraction job ⚠**
- Scope: doc 12 §3/§4 fully: dispatch loop, pause/resume/cancel, budget/error-rate stops, heartbeats, counters, SignalR throttled progress, completion notifications; `JobRecoveryService`.
- Tests: kill-and-recover integration (Testcontainers + WireMock): start 100-task run, kill host mid-run, restart, assert completion without duplicate extraction rows; pause/resume; budget stop with overshoot bound; error-rate stop.

**T11.3 Processing UI** — runs list + Run Details (W8) live view, controls, retry-failed flow, task table.

## E12 — Review Policy & Statuses

**T12.1 Post-extraction review routing** — apply `review_policy` (ReviewAll / BelowConfidenceThreshold incl. auto-approve path with `autoApproved` flag + delivery status flip); project settings UI for policy + budgets + `allowBulkApprove`.
- Edge cases: threshold mode with missing confidences (treated low); alwaysReviewFields honored even in threshold mode.

## E13 — SafeHttp (security core; built early, consumed by E4/E8/E14/E16)

**T13.1 SafeHttpClientFactory ⚠**
- Scope: doc 14 pipeline (vetting, pinning, redirects, caps, header allowlist, proxy resolution, profiles), settings for egress lists + global proxy (+ Settings UI section).
- Tests: full SSRF suite from doc 16 §tests (this is the reference test set; runs in CI).

## E14 — Assignments & Review Ops

**T14.1 Assignment batches ⚠**
- Scope: preview + create endpoints (strategies Even/ByCount/RoundRobin, scope resolution, per-row conflict results), sync path ≤1k + `AssignmentDistributionJob` above, notifications fan-out (in-app now, Telegram after E16), Assignment Manager UI (W9).
- Edge cases: reviewer removed from project mid-batch (rows fail per-row); records leaving Unassigned between preview and create (skipped + reported).

**T14.2 Assignment mutations** — bulk-action endpoint (reassign/unassign/cancel/priority/due/release-locks + move-unfinished), R19 return-path linkage, audit; UI actions.

**T14.3 Review Operations dashboards** — global + project endpoints (SQL aggregates incl. edit/rejection rates from `field_changes`/events) + UI (W10) + fairness note.

## E15 — Reviewer App

**T15.1 Locking + heartbeat ⚠** — lock endpoints per doc 09 §10; auto-expiry semantics; admin force-release integration; SignalR `RecordLockChanged`.
- Tests: contention matrix (same user two tabs, two users, expiry takeover, stale-token writes).

**T15.2 Review write endpoints ⚠** — draft PUT, approve, reject, return, skip, bulk (R20) exactly per doc 11 contracts; `field_changes` diff (D18); next-record picker; review events.
- Tests: approve transaction atomicity (kill mid-transaction → consistent); diff normalization vectors; bulk partial results.

**T15.3 My Tasks + Review Queue UI** — cross-project tasks endpoint + pages; queue filters; continue-flow.

**T15.4 Focus Review UI ⚠** — W11 full spec: split layout, evidence highlight, autosave (30 s dirty), action bar, shortcuts, conflict panel (409 flow), end-of-queue state, mobile/Mini-App stacked layout, prefetch next.

**T15.5 Table Review UI** — W12: column config, inline scalar edit (per-row transient lock+save), drawer for complex fields, bulk actions with per-row result markers.

**T15.6 Completed + Progress pages** — endpoints + UI.

## E16 — Telegram

**T16.1 Bot core + settings** — Telegram.Bot wiring, webhook route + secret validation, polling service, Settings UI panel (token replace, mode, set-webhook, test), update idempotency.
**T16.2 Account linking ⚠** — link codes (hash/TTL/rate-limit), `/start` handling, unlink/revoke, linking UI (reviewer settings + user admin), audit; takeover tests (doc 16 §tests item 5).
**T16.3 Commands + notifications** — `/tasks /next /progress /open /help`, NotificationDispatchJob Telegram sends (aggregated assignment messages, run completion, due reminders recurring job, backlog threshold, dead-letter alerts), MarkdownV2 escaping helper (unit-tested with hostile names).
**T16.4 Mini App auth + adapter ⚠** — `POST /auth/telegram-miniapp` (HMAC validation, age window, revoked-link rejection), `/tg` entry, theme mapping, BackButton, startapp deep links; Playwright test with forged/valid initData fixtures.

## E17 — Delivery

**T17.1 Excel export ⚠** — export template store, run creation, `ExcelExportJob` (streaming, D23 layout: dot-flatten, join, child sheets; sanitization), download route (auth + expiry → 410), Export Center UI (W13), retention cleanup hookup.
- Tests: golden-file export of demo dataset (structure + sanitization asserted); 50k-row memory test; interrupted-job cleanup.

**T17.2 Output connector + template engine ⚠** — connector CRUD/test/dry-run, mustache-subset engine (unit-tested against injection attempts), payload preview UI, builder UI.

**T17.3 Delivery runs ⚠** — DeliveryRunJob + ApiDeliveryJob per doc 12 §7 with D15 idempotency, retry/dead-letter, batch mode; deliveries UI (run detail, per-record statuses, retry buttons); record delivery-status integration.
- Tests: WireMock delivery matrix (2xx, 4xx permanent, 5xx retry→success, timeout-unknown-outcome idempotency replay, batch partial failure); Delivered-unique constraint race test.

## E18 — Dashboards, Usage, Audit, Notifications

**T18.1 Audit module** — `IAuditWriter` implementation (same-transaction), query endpoints + UI + CSV export. (Writer contract exists from T0.3; all earlier epics already call it — this task delivers storage/query/UI and backfills any missed call sites via checklist.)
**T18.2 Usage & costs** — usage aggregates endpoints, Usage UI (W14), budget bars, price-table integration, cost-per-approved metric.
**T18.3 Admin Dashboard + Project Overview completion** — real funnel/counters/charts wiring (W1/W2), provider health strip, setup checklist logic.
**T18.4 Notification center** — endpoints, bell UI, SignalR `NotificationReceived`, mark-read.

## E19 — Realtime & Polling Fallback

**T19.1 SignalR hub + client** — hub with project/user groups + auth; typed client wrapper with auto-reconnect, group re-join, and a `LiveQuery` helper that transparently switches to 10 s polling when disconnected (used by all live pages). Connection state indicator in UI.

## E20 — PWA & Offline

**T20.1 PWA shell** — vite-plugin-pwa manifest/icons/SW (precache shell, runtime cache: static SWR; GET queue endpoints network-first with cache fallback marked stale), install prompt, update toast → skipWaiting flow, online/offline indicator, secure logout (clear caches + IndexedDB + tokens; blocked while unsynced drafts, R30/E30).
**T20.2 Offline drafts + sync ⚠** — IndexedDB store, offline draft capture, reconnect sync engine (version-gated per D17), conflict UI, pending-sync badges; decisions disabled offline.
- Tests: Playwright offline-mode scenarios (edit offline → sync; conflict path; logout guard).

## E21 — Deployment & Docs

**T21.1 Docker & compose** — multi-stage Dockerfile (SPA build → publish), compose dev + prod (app/postgres/caddy, volumes, healthchecks), `.env.example` (every variable documented), non-root container user.
**T21.2 Ops runbook** — README (quickstart) + `docs/SETUP.md` per doc 23: first run, admin bootstrap, Telegram webhook, backup/restore scripts (`scripts/backup.sh`, `restore.sh`), upgrade procedure.
**T21.3 Demo seed** — `SEED_DEMO=true` seeding of doc 22 scenario (idempotent, dev-guarded).

## E22 — Quality Gate (final sweep; not a dumping ground — continuous testing happened per task)

**T22.1 E2E suite** — Playwright: the doc 22 demo flow end-to-end against compose stack with WireMock-backed fake provider (env-switched base URL): import → process → assign → review (focus+table+bulk) → export (file content asserted) → delivery (received payload asserted) → audit/usage visible.
**T22.2 Security regression pack** — doc 16 §tests items 1–7 consolidated in CI.
**T22.3 Performance sanity** — 50k import, 5k-record run against mock provider (concurrency 16), queue latency, dashboard query times < 500 ms; documented results.
**T22.4 UX acceptance pass** — checklist audit of every page vs doc 06 (states, mobile, a11y smoke via axe), fix fallout.
