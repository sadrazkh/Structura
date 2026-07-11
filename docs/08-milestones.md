# 08 — MVP Implementation Milestones

Six milestones. Each ends with a **runnable, testable deliverable** (demo criteria listed). Tests are written inside each milestone, not deferred. Stack scaffolding rules in doc 09.

## M1 — Foundation: auth, users, projects (runs in Docker from day one)

Build: solution skeleton (single `Structura.Web` + `ClientApp` + `Structura.Tests`), Postgres + EF migrations at startup (advisory lock), Serilog + problem+json middleware, bootstrap admin from env, JWT auth (login/refresh/rotation/change-password/lockout), Users CRUD (Administrator), Projects CRUD + members, role/membership authorization filters, SPA shell: design tokens (light/dark) + base components (Button, Input, Select, Table, Dialog, Toast, EmptyState, Skeleton, Badge), Login + Users + Projects + Project Settings pages, Dockerfile + compose (app/postgres/caddy) + `.env.example`.
**Deliverable/demo:** `docker compose up` → login as bootstrap admin → forced password change → create PM + 2 reviewers → create project → add members. Refresh-rotation and role-denial integration tests green.

## M2 — Configuration & data in: schema, AI settings, import

Build: schema fields model + validation + `PUT /schema` (version bump), Schema Builder UI (list + editor + live form preview using the dynamic form renderer built here for the 8 types), AI config + prompt config endpoints (encrypted key, masking) + Test Connection (real provider call) + AI Settings UI, SafeHttpClient (SSRF suite), upload/preview/mapping endpoints + ImportWorker (streaming, dedup, errors, cancel, resume) + Import UI with live progress (SignalR hub introduced here), manual input (single + bulk paste), API input (config/test/fetch), Records page (filters, keyset paging, detail drawer, delete guard).
**Deliverable/demo:** define the incident-report schema → Test Connection to OpenRouter ✓ → import a 10k-row Excel → watch progress → records browsable/filterable. Import restart-resume + SSRF + dedup tests green.

## M3 — AI processing engine

Build: JSON Schema generator + prompt builder + provider adapter (OpenRouter/NVIDIA, structured-output with plain fallback, transport retry) + parse/repair/re-ask + output validator, `ProcessingWorker` (claiming, concurrency, counters, finalization, restart recovery), runs endpoints (create with scope + snapshot, cancel, retry-failed), Processing Runs UI (list + live detail: progress, errors, tokens), record detail shows AI output + attempts.
**Deliverable/demo:** select 50 records → Process → real model fills fields → run shows live progress, token usage, a failed record with readable error → Retry Failed works → kill the container mid-run, restart, run completes without duplicates. WireMock provider-contract + recovery + repair-corpus tests green.

## M4 — Assignment & reviewer app

Build: assignment endpoints (single / distribute-evenly / unassign / reassign, per-record results), review endpoints (open→InReview, save with version check, approve with server-side validation, reject/reprocess with note, next-record, bulk-approve with re-validation, progress), reprocess loop (ReprocessRequested → run scope → back to same reviewer), Reviewer SPA: My Tasks, Record List (+ bulk approve), Focus Review (original text `dir=auto` + dynamic form + AI-value display + actions + prev/next + auto-advance + keyboard), Progress, Settings (password); Review Status + Assignments admin pages.
**Deliverable/demo:** distribute 100 records to 2 reviewers → reviewer logs in on a phone → reviews sequentially, edits a wrong value, approves, rejects with note, returns one for reprocess → bulk-approves 10 clean rows → admin sees per-reviewer progress; human edits stored in `final_output`, AI output untouched (asserted by test). Version-conflict test green.

## M5 — Output: Excel export & API delivery

Build: streaming Excel export endpoint (columns per doc 05, sanitization) + Export page, API output config + body-template renderer (`{{record.id}}`, `{{record.externalId}}`, `{{output.<key>}}`, `{{review.reviewer}}`, `{{review.approvedAt}}`) + Test Request (render / real send), `DeliveryWorker` + delivery table UI + retry-failed, Dashboard page (real counters + active run).
**Deliverable/demo:** export approved records → open the file (correct columns, injected-formula row neutralized) → configure a test endpoint → Start Delivery → statuses turn Delivered, one forced failure retried successfully. Golden-file export + delivery-retry tests green.

## M6 — Telegram, PWA polish, ship

Build: bot (webhook + polling, settings UI, set-webhook, getMe test), account linking (codes, unlink, admin revoke), commands (`/start /tasks /next /help`), assignment/reprocess/run-finished notifications, Mini App entry `/tg` (initData auth, theme mapping, BackButton, startapp deep links), PWA manifest + service worker (installable, app shell precache, update toast) — no offline sync, demo seed (`SEED_DEMO=true`: users, sample project, 60 mixed fa/en records incl. edge rows), README + SETUP guide (install, env vars, webhook, backup/restore), final Playwright E2E of the doc 09 completion flow + security regression pack.
**Deliverable/demo:** the full 14-step MVP completion flow (doc 09) executed on a clean machine from README alone, including a reviewer receiving a Telegram notification and approving inside the Mini App.

**Dependency chain:** M1 → M2 → M3 → M4 → M5 → M6 (strict; no parallel milestones needed).
