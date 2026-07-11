# 09 — Implementation Handoff for Coding AI

This is the binding contract. Build **Structura V1** exactly per `docs/` (this folder). The archived enterprise plan (`../docs-archive-enterprise/`) is background reading only — when in doubt, this lean spec wins. Do not add out-of-scope features (doc 01 list), do not leave anything mock.

## Stack (final)
.NET 10 LTS · ASP.NET Core Minimal APIs · EF Core + Npgsql · PostgreSQL 16 · SignalR · ASP.NET Core Identity hashing + JWT (15 min access / rotating 14 d refresh) · FluentValidation · Serilog · MiniExcel + CsvHelper · Telegram.Bot · Vue 3 + TypeScript + Vite + Pinia + Vue Router + TanStack Query · Tailwind CSS · vite-plugin-pwa · xUnit + Testcontainers(Postgres) + WireMock.Net + Playwright · Docker Compose (app + postgres + caddy). **No Hangfire, no Redis, no message broker, no microservices.**

## Structure (final)
```
Structura.sln
├─ src/Structura.Web/            # ONE project: Features/<Area>/... vertical slices (Endpoint+Handler+Validator),
│  │                             # Domain/ (entities, status guards), Infrastructure/ (workers, SafeHttp, AI adapter,
│  │                             # excel, telegram, secrets), Persistence/ (DbContext, migrations, seed)
│  └─ ClientApp/                 # Vue SPA: app/(router,layouts) design-system/ api/ features/(admin, reviewer, shared DynamicForm) pwa/ telegram/
├─ tests/Structura.Tests/        # unit + integration (Testcontainers, WireMock)
├─ tests/Structura.E2e/          # Playwright
└─ docker/                       # Dockerfile, docker-compose.yml, compose.prod.yml, Caddyfile, .env.example
```

## Build order
Milestones M1→M6 (doc 08), strictly sequential. Within each: DB → backend endpoints/workers → tests → frontend → integration check against the milestone demo.

## Non-negotiable rules
1. No mock data; every page wired to the real backend; all CRUD real; real EF migrations.
2. Real auth/authorization enforced in backend (role + membership + reviewer scoping in queries).
3. Background processing real and durable: DB-as-queue workers (doc 05), restart-resume proven by a kill-restart test.
4. OpenRouter and NVIDIA integrations real (one OpenAI-compatible adapter, structured output with plain fallback, repair + one re-ask retry, then Failed).
5. Excel/CSV import streaming (never whole-file in memory); Excel export streaming + formula-injection sanitization.
6. API input/output connectors really send requests through `SafeHttpClient` (SSRF rules of doc 07 §7 mandatory).
7. Telegram bot actually runs (webhook + polling modes); Mini App = same SPA at `/tg` with initData HMAC auth; linking codes hashed/single-use/expiring.
8. PWA genuinely installable (manifest + SW + update toast). No offline sync.
9. Secrets: Data Protection-encrypted at rest, masked in responses, redacted in logs, none in the repo. JWT key + bootstrap admin via env.
10. Statuses per doc 01 only; invalid transitions rejected in backend (409 `invalid_state`); optimistic `version` on records (409 `version_conflict`, no silent overwrite).
11. AI output (`extraction_results.output`) and human output (`records.final_output`) stay separate — approve never mutates extraction rows.
12. Errors: problem+json with stable codes; failures visible in run/import/delivery tables, never toast-only.
13. Tests written and executed per milestone: unit (validator, prompt builder, JSON Schema gen, repair corpus, status guards, sanitizer, template renderer), integration (auth rotation, authz denials, import resume, run recovery kill-test, provider contract via WireMock, delivery retry, SSRF suite), E2E (completion flow below). CI: build → test → e2e.
14. UI: English chrome, `dir="auto"` for record content (Persian+English), light+dark themes, loading/empty/error states on every page, mobile-first reviewer area, no dead buttons, no TODO/placeholder in the final result.
15. README + `docs/SETUP.md`: clone → configure `.env` → `docker compose up` → working system in ≤30 minutes, including Telegram webhook setup and backup/restore commands.

## MVP completion criteria (all must pass in one end-to-end run — this is the Playwright E2E)
1. Admin logs in (bootstrap → forced password change). 2. Creates a project. 3. Defines dynamic fields (all 8 types exercised). 4. Configures OpenRouter or NVIDIA (Test Connection ✓). 5. Imports an Excel file with ID + Text (10k rows OK, duplicates skipped). 6. Sends all records to AI. 7. AI really returns schema-conforming JSON (WireMock in CI, real provider in manual verification). 8. Results persist (extraction rows + statuses). 9. Admin assigns records to reviewers (single + distribute). 10. Reviewer opens a record in the PWA or Mini App. 11. Reviewer edits and approves / rejects / requests reprocess (+ bulk approve). 12. Admin exports approved records to Excel (correct columns, sanitized). 13. If configured, approved records deliver to the external API with retry on failure. 14. Everything runs via `docker compose up` on a clean machine following the README.

**Definition of done:** the criteria above demonstrated end-to-end, all tests green, and no feature in this spec existing only as UI.
