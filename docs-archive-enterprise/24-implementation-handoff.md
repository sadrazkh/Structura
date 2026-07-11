# 24 — Implementation Handoff for Coding AI

This section is an **executable directive**. You (the coding AI) are building Structura to production quality. The documents in `docs/` are the specification; this file is the binding contract. Where any conflict appears, this file wins, then doc 02, then the specific design doc.

## Final technology stack
.NET 10 LTS · ASP.NET Core Minimal APIs · EF Core + Npgsql · PostgreSQL 16 · Hangfire (PostgreSQL storage) · SignalR · ASP.NET Core Identity + JWT (access 15 min / rotating refresh 14 d) · FluentValidation · Serilog · MiniExcel + CsvHelper · Telegram.Bot · Vue 3 + TypeScript + Vite + Pinia + Vue Router + TanStack Query · Tailwind CSS v4 + Reka UI · vite-plugin-pwa + IndexedDB · xUnit + Testcontainers + WireMock.Net + Playwright + Vitest · Docker + Compose + Caddy.

## Final architecture
Modular monolith, vertical slices (doc 02 D1/D2). One host (`Structura.Web`), one DbContext (`Structura.Persistence`), module class libraries under `src/Modules/` exposing `Contracts` only. Solution/module/frontend structure exactly as doc 02 — treat it as scaffolding instructions.

## Binding decisions you must not re-open
All 25 decisions in doc 02; database schema in doc 10; state machines in doc 09; API catalog and conventions in doc 11; job semantics in doc 12; AI pipeline in doc 13; connector/SSRF rules in doc 14; Telegram design in doc 15; security controls in doc 16; error codes in doc 17. Canonical names (entities, tables, endpoints, permissions, statuses, error codes) are used verbatim.

## Core interfaces to implement first
`ISecretProtector` · `IAuditWriter` · `ICurrentUser` · `IClock` · `SafeHttpClientFactory` (profiles `ConnectorEgress`/`AiEgress`) · `IAiCompletionProvider` + `IAiProviderRegistry` + `RateGate` · `IInputConnector` / `IOutputConnector` registries · `SchemaToJsonSchemaConverter` · `SchemaOutputValidator` (+ shared test vectors consumed by the TS mirror) · `PromptBuilder` · `<X>StateMachine` guards.

## API conventions
Doc 11 header section, in full: `/api/v1`, problem+json with stable `code`, `version` optimistic concurrency in bodies, `Idempotency-Key` on run-starting POSTs, keyset pagination `{items, nextCursor}`, `ProjectAccessFilter` on all project routes, `X-Correlation-Id` everywhere, OpenAPI committed and driving generated TS types.

## Background jobs
Hangfire + domain state tables (doc 12). Every job idempotent, heartbeats every 15 s, `JobRecoveryService` at startup + 2 min interval. Queues: `processing/default/delivery/maintenance` with the stated worker counts.

## Security rules (non-negotiable)
Everything in doc 16 baseline: encrypted secrets via DataProtection (`/data/keys`), masked responses, replace-only secret writes, log redaction, SSRF pipeline with IP pinning on **all** outbound connector/provider/Telegram traffic, Excel formula sanitization, prompt-injection layering (nonce tags + structured output + validator), rate limits, upload validation, reviewer query-scoping, last-admin guard, Hangfire dashboard gated, no secret in repo/compose/snapshots/logs.

## UI
English-only chrome; record content bidirectional with `dir="auto"` and bundled Vazirmatn for Persian text; the design system of doc 06 built from tokens (light+dark, Telegram theme mapping); page specs of doc 06 and wireframes of doc 07 are the layout source of truth; every list has empty/loading/error states; Focus Review keyboard shortcuts implemented.

## PWA & Telegram
PWA per doc 06/19-E20 (installable, offline shell, version-gated offline draft sync with conflict UI, update prompt, secure logout). Telegram per doc 15 (webhook + polling modes, hashed one-time linking codes, initData HMAC auth at `/tg`, notifications via dispatch job). Mini App = same SPA, no fork.

## Testing & deployment requirements
Doc 21 in full (per-task tests, mandatory packs: concurrency/idempotency/recovery/permission/data-integrity, WireMock provider contract suite, Playwright E2E of the doc 22 demo flow, axe accessibility). Doc 23 in full (Dockerfile, compose dev+prod, Caddy, `.env.example`, migrations at startup under advisory lock, bootstrap admin, backup/restore scripts, health/readiness, SETUP guide).

## Implementation order
Follow doc 20 exactly. Do not start a task before its dependencies. Within a task, deliver backend → tests → frontend → integration.

## Coding conventions
C#: file-scoped namespaces, nullable enabled, `dotnet format` clean; async everywhere with `CancellationToken`; no static mutable state; exceptions only for exceptional paths (domain results via `Result<T>`); comments only for non-obvious constraints. TS: strict mode, ESLint + no `any` in feature code, composables over mixins, no `v-html`. Commits per task ID (`T11.2: orchestrator + recovery`).

## Rules (verbatim requirements from the product owner)
1. Never deliver with mock data. 2. Every page is wired to the real backend. 3. All CRUD is real. 4. Real EF migrations. 5. Real authentication & authorization. 6. Real durable background jobs. 7. Real OpenRouter integration. 8. Real NVIDIA-compatible integration. 9. Real Excel import & export. 10. API connectors really send requests. 11. Telegram bot actually runs. 12. PWA actually installable. 13. Pause/Resume/Retry/Recovery actually work. 14. Error handling beyond cosmetic toasts. 15. Permissions enforced in backend. 16. Validation not frontend-only. 17. API keys encrypted. 18. No secrets in source. 19. Tests written **and executed**. 20. Docker setup runs. 21. Complete README + Setup Guide. 22. Project runs after clone via documented steps. 23. Complete, professional UI. 24. No dead buttons or pages. 25. No TODO/placeholder/fake implementation in the final MVP.

## MVP completion criteria (all must hold, demonstrated end-to-end)
Admin can: log in; create users/reviewers; create a project; define a dynamic schema; configure a provider; configure prompts; test a sample in Playground; import Excel; see records persisted; run a real batch; watch AI actually process; see parsed+validated results; retry failed jobs; assign records to reviewers. Reviewer: receives Telegram notification; opens record from PWA or Mini App; edits values; approves/rejects; human edits stored separately from AI output. Admin: exports approved records to Excel; delivers to an external API; failed deliveries retry; audit log populated; usage/costs displayed; permissions enforced; project runs with Docker; migrations work; README explains install; core tests pass; **no primary feature is display-only**.

## Definition of a production-ready result
A fresh machine with Docker can clone the repo, follow `docs/SETUP.md`, and within 30 minutes have: HTTPS-served app, bootstrap admin, a configured provider, the demo project processing real records through a real model, a reviewer approving on a phone (PWA or Telegram Mini App), an Excel file and an API delivery produced from those approvals — with every action visible in the audit log and every failure mode in this spec handled as designed.
