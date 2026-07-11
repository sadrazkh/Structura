# 02 — Architecture Decisions (Final and Binding)

Every decision below is **final**. The coding AI must not re-open these choices. Format: context → decision → rationale → consequences.

## Final Technology Stack

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | Fall back to .NET 8 LTS only if the build environment cannot install .NET 10; no preview features either way |
| Web framework | ASP.NET Core Minimal APIs + endpoint groups | One HTTP host; controllers not used |
| ORM | Entity Framework Core + Npgsql | Single `AppDbContext`, code-first migrations |
| Database | PostgreSQL 16+ | Only datastore (relational + JSONB + job storage) |
| Background jobs | **Hangfire** with `Hangfire.PostgreSql` storage | + domain job-state tables as source of truth (D3) |
| Real-time | SignalR (in-process, single node) | Redis backplane documented as future scale-out step |
| Auth | ASP.NET Core Identity (EF stores) + JWT access/refresh tokens | Mini App: Telegram `initData` exchange → same JWT |
| Validation | FluentValidation (requests) + custom `SchemaOutputValidator` (dynamic outputs) | |
| Logging | Serilog (structured, JSON console + rolling file) | Correlation ID enrichment |
| Telemetry | OpenTelemetry-ready wiring (traces/metrics abstractions), health checks via `AspNetCore.HealthChecks` | Exporters optional |
| Excel/CSV | **MiniExcel** (streaming XLSX read/write) + **CsvHelper** (CSV) | Never load whole files in memory |
| Telegram | **Telegram.Bot** library; webhook or long-polling (config) | |
| Frontend | Vue 3 + TypeScript + Vite + Pinia + Vue Router + TanStack Query (vue-query) | Single SPA, code-split admin/reviewer |
| Styling/UI | Tailwind CSS v4 + Reka UI (headless components) + custom design system | No admin template packages |
| PWA | vite-plugin-pwa (Workbox) + IndexedDB (`idb`) for offline drafts | |
| API types | OpenAPI generated from endpoints; `openapi-typescript` generates the TS client types at build | |
| Tests | xUnit, FluentAssertions, Testcontainers (PostgreSQL), WireMock.Net, Playwright, Vitest | |
| Packaging | Docker multi-stage build, Docker Compose (app + postgres + caddy) | Single deployable unit |

## Solution Structure

```
Structura.sln
├─ src/
│  ├─ Structura.Web/                     # Host: Minimal APIs, SignalR hubs, Hangfire server + dashboard,
│  │  │                                  # auth, middleware, serves built SPA from wwwroot
│  │  └─ ClientApp/                      # Vue 3 + TS SPA (see frontend structure below)
│  ├─ Structura.SharedKernel/            # Result<T>, ErrorCodes, ICurrentUser, IClock, ISecretProtector,
│  │                                     # domain event contracts, pagination primitives, guard clauses
│  ├─ Structura.Persistence/             # AppDbContext, entity configurations, migrations, seeding
│  └─ Modules/
│     ├─ Structura.Modules.Identity/     # users, roles, permissions, auth, refresh tokens
│     ├─ Structura.Modules.Projects/     # projects, members, settings, wizard drafts
│     ├─ Structura.Modules.Schemas/      # schema versions, field spec model, JSON Schema generation
│     ├─ Structura.Modules.Prompts/      # prompt versions, prompt builder
│     ├─ Structura.Modules.Providers/    # AI providers, adapters, model prices, safe AI HTTP
│     ├─ Structura.Modules.Ingestion/    # Excel/CSV import, manual input, API input connectors
│     ├─ Structura.Modules.Records/      # record store, filtering, locking, statuses
│     ├─ Structura.Modules.Processing/   # processing runs, tasks, extraction pipeline, budgets
│     ├─ Structura.Modules.Reviews/      # assignments, review actions, drafts, edit-rate metrics
│     ├─ Structura.Modules.Delivery/     # Excel export, output connectors, API deliveries
│     ├─ Structura.Modules.Telegram/     # bot, linking, mini-app auth, webhook/polling
│     ├─ Structura.Modules.Notifications/# notification center + dispatch
│     ├─ Structura.Modules.Usage/        # usage events, cost aggregation
│     └─ Structura.Modules.Audit/        # audit events, query API
├─ tests/
│  ├─ Structura.UnitTests/
│  ├─ Structura.IntegrationTests/        # Testcontainers Postgres + WireMock
│  └─ Structura.E2eTests/                # Playwright
├─ docker/                               # Dockerfile, docker-compose.yml, compose.prod.yml, Caddyfile
└─ docs/
```

Module anatomy (every module identical):

```
Structura.Modules.<Name>/
├─ Domain/          # entities, value objects, domain services, state machines
├─ Features/        # vertical slices: <Verb><Noun>/ {Endpoint.cs, Request.cs, Handler.cs, Validator.cs}
├─ Infrastructure/  # module-specific services (adapters, file parsers, http)
└─ ModuleSetup.cs   # DI registration + endpoint group mapping (called by Structura.Web)
```

Frontend structure:

```
ClientApp/src/
├─ app/                  # router, auth guards, layouts (AdminLayout, ReviewerLayout, TgLayout)
├─ design-system/        # tokens.css, Button, Input, Select, Table, Card, Dialog, Toast, Badge,
│                        # EmptyState, Skeleton, Tabs, Drawer, ConfirmDialog, FormField ...
├─ api/                  # fetch wrapper (auth, problem+json handling), generated types, query hooks
├─ features/
│  ├─ admin/             # one folder per admin page group
│  ├─ reviewer/          # tasks, focus-review, table-review, progress, settings, telegram-link
│  └─ shared/            # DynamicForm renderer, auth pages, notifications, realtime (SignalR client)
├─ pwa/                  # SW registration, offline drafts store (IndexedDB), sync engine
└─ telegram/             # Telegram WebApp SDK adapter (theme, initData auth, BackButton)
```

---

## The 25 Decisions

### D1. Modular Monolith structure
**Decision:** One solution, one ASP.NET Core host, one database, one EF Core `AppDbContext`. Modules are separate class-library projects with vertical slices inside. Module boundaries are enforced at the application layer (modules expose public `Contracts` interfaces/DTOs; other modules may reference only `Contracts` + `SharedKernel`), **not** at the persistence layer.
**Rationale:** Cross-module transactions (record status + audit + usage in one commit) are constant in this domain; multiple DbContexts would force distributed-transaction complexity for zero MVP benefit. Project-level references make boundary violations compile-time visible.
**Consequences:** Single migrations project; a module cannot be extracted to a service without persistence work — acceptable and documented.

### D2. Vertical Slice vs Clean Architecture
**Decision:** Vertical Slice Architecture inside each module. Each feature = one folder with `Endpoint` (minimal API mapping), `Request/Response` DTOs, `Handler` (plain class, constructor-injected), `Validator` (FluentValidation). No MediatR, no global onion layers. Domain entities and state machines live in `Domain/` per module and are shared across that module's slices.
**Rationale:** ~150 endpoints of mostly CRUD+orchestration; slices keep changes local and are ideal for AI-driven implementation. MediatR adds indirection without value here.
**Consequences:** Some duplication between slices is accepted; shared logic is pulled into module domain services only when used ≥2 times.

### D3. Background job technology
**Decision:** **Hangfire with PostgreSQL storage** for execution (retries, scheduling, dashboard at `/hangfire` for SystemAdministrator only), combined with **domain state tables** (`processing_runs`, `processing_tasks`, `import_runs`, `export_runs`, `delivery_runs`, `api_deliveries`) as the single source of truth. Hangfire jobs are thin, idempotent executors that receive domain IDs, re-read state, and honor pause/cancel flags. A startup `JobRecoveryService` requeues domain tasks stuck in `Running` with an expired heartbeat.
**Rationale:** Durable, Postgres-only (no Redis dependency), battle-tested retries and recurring jobs. Domain tables give us pause/resume/cancel/progress semantics Hangfire alone lacks.
**Consequences:** Every job body must be safe to run twice (enforced by unique constraints + status checks). Per-provider rate limiting is app-level (`System.Threading.RateLimiting`), not Hangfire queues.

### D4. Dynamic schema storage
**Decision:** `schema_versions` table; the full field tree is one immutable JSONB `definition` document per version (see [08-domain-model.md](08-domain-model.md) for the exact format). No per-field rows.
**Rationale:** The schema is read/written as a whole; field-level rows would add joins and migration pain for zero query benefit (fields are never queried independently of their version).
**Consequences:** Server-side C# model (`FieldSpec`) deserializes/validates the document; a JSON Schema for the definition format itself is kept in the repo and unit-tested.

### D5. Relational vs JSONB storage
**Decision:** Hybrid, with a hard rule: **anything the system filters, joins, counts, or state-transitions on is a relational column; anything shaped by the admin-defined schema is JSONB.** Relational: users, projects, records (statuses, external_id, assignment pointers), runs, tasks, assignments, deliveries, usage, audit. JSONB: schema definitions, prompt configs, parsed AI output, field meta, human outputs, validation results, connector configs, run snapshots, import mappings.
**Rationale:** Dynamic fields make fixed columns impossible; statuses/counters in JSONB would make every operational query slow and unindexable.
**Consequences:** GIN index on `records.final_output` (jsonb_path_ops) enables dynamic-field filtering at 50k-record scale without projections. A projection/materialization layer is explicitly deferred to Phase 2.

### D6. AI output storage
**Decision:** `extraction_results` table, one row per extraction attempt: `parsed_output` JSONB (values keyed by field key), `field_meta` JSONB (`{fieldKey: {confidence, evidence}}`), `validation_result` JSONB, `raw_response` TEXT (retention-limited), plus relational columns for run/version/provider/model/tokens/cost/status/duration. **No per-field result table.**
**Rationale:** 50k records × 20 fields = 1M rows per run in a field table — cost without benefit; per-field queries are served from JSONB, and edit-rate metrics are computed once at approve time (D18).
**Consequences:** Records point to `latest_extraction_id`; history of attempts is preserved (one row per attempt).

### D7. Human-edited output storage
**Decision:** `record_reviews` table (one active row per record): `draft_output` JSONB (reviewer working copy), `final_output` JSONB (set on approve), `field_changes` JSONB (computed diff vs AI output), decision, note, reviewer, timestamps, `version` concurrency token. AI `extraction_results` are **never mutated**. On approve, `final_output` is also denormalized onto `records.final_output` (written only inside the approve transaction) for fast export/filtering.
**Rationale:** Clean separation AI vs human; denormalization avoids join-heavy export queries; single-writer transaction prevents drift.
**Consequences:** Re-review after reprocessing archives the old `record_reviews` row content into a `review_events` payload before reset.

### D8. Schema versioning
**Decision:** Draft → Published → Archived lifecycle. Exactly one Draft may exist; editing a Published version copies it into a new Draft. Published versions are immutable (DB-guarded: update trigger/interceptor rejects definition changes). Records, runs, and extraction results reference `schema_version_id`. A processing run pins its schema version at start; publishing a new version never affects running work.
**Rationale:** Reproducibility and safe evolution, as required.
**Consequences:** The Reviewer form always renders using the schema version of the record's latest extraction (not the newest published version).

### D9. Prompt versioning
**Decision:** Identical mechanism to D8: `prompt_versions` with Draft/Published/Archived, immutable once published, referenced by runs/results/test-case runs.
**Rationale:** Symmetry; same reproducibility requirement.
**Consequences:** Playground can run against the current Draft explicitly (labelled), but processing runs only accept Published versions.

### D10. Processing run snapshots
**Decision:** `processing_runs.config_snapshot` JSONB stores: schema_version_id + prompt_version_id (immutable references), provider id/type/baseUrl/model, generation settings (temperature, topP, maxOutputTokens), concurrency, retry policy, budget limits, scope filter, and missing-value behaviors — everything needed to reproduce the run **except secrets** (API keys are never snapshotted).
**Rationale:** Immutable version references + snapshot of mutable provider/generation settings = full reproducibility with minimal duplication.
**Consequences:** Run detail page renders entirely from the snapshot; later provider edits don't falsify history.

### D11. Large Excel processing
**Decision:** Import: MiniExcel streaming reader (XLSX) / CsvHelper (CSV); rows processed in chunks of 500 per DB transaction (bulk insert with `COPY` via Npgsql binary import where possible); file first saved to `/data/uploads`, then parsed by the background `FileImportJob`. Preview reads only the first 50 rows synchronously. Export: MiniExcel streaming writer to `/data/exports`. Hard limits: upload ≤ 100 MB, ≤ 200,000 rows (config), per-cell text ≤ 512 KB.
**Rationale:** Meets the 50k-row target with flat memory; MiniExcel is the one library that streams both directions.
**Consequences:** Import is always asynchronous after mapping confirmation; progress via SignalR; row errors collected into `import_row_errors` and downloadable as CSV.

### D12 + D13. API connector security and SSRF prevention
**Decision:** All connector traffic goes through one `SafeHttpClientFactory` that enforces: scheme allowlist (`https`; `http` only when `ALLOW_INSECURE_HTTP=true` for dev), DNS resolution with IP vetting (block loopback, RFC1918, link-local 169.254.0.0/16 incl. cloud metadata, ULA/fc00::/7, multicast, 100.64.0.0/10), **pinned connection** to the vetted IP via `SocketsHttpHandler.ConnectCallback` (defeats DNS rebinding), redirects disabled and re-validated manually (max 3 hops), response size cap (10 MB default), request timeout (30 s default, configurable per connector ≤ 120 s), custom header allowlist (deny `Host`, `Content-Length`, `Transfer-Encoding`; auth headers only via typed auth config), TLS certificate validation always on, optional egress allowlist/denylist per installation, and per-connector proxy override falling back to the global proxy (R2).
**Rationale:** Single choke point; every mandated SSRF control implemented once and tested once.
**Consequences:** AI provider calls use the same factory with a relaxed policy profile (`AiEgress`: admin-entered base URLs still IP-vetted unless `ALLOW_PRIVATE_AI_ENDPOINTS=true` for Ollama-style future use).

### D14. Job recovery after restart
**Decision:** Three layers: (1) Hangfire persists and re-runs its jobs after restart; (2) every long-running domain task writes `heartbeat_at` every 15 s; (3) `JobRecoveryService` (hosted service, runs at startup + every 2 min) finds domain tasks in `Running` with heartbeat older than 2 min, marks them `Queued`, and re-enqueues — safely, because task bodies are idempotent (unique `(run_id, record_id, attempt)` on extraction writes; status transition guards).
**Rationale:** Meets "resume after restart" without inventing a custom queue.
**Consequences:** Worst case a record is extracted twice; the second write is discarded by the unique constraint + status guard, and cost is still recorded (documented behavior).

### D15. Duplicate API submission prevention
**Decision:** One `api_deliveries` row per (output_connector, record, delivery_run). Partial unique index `(output_connector_id, record_id) WHERE status = 'Delivered'` makes double-delivery impossible at the DB level. New delivery runs skip already-delivered records unless `redeliver: true` (which archives the old row to `Superseded`). Each HTTP request carries `Idempotency-Key: <api_delivery.id>` (stable across retries of the same delivery row).
**Rationale:** DB-level guarantee beats application checks; stable idempotency key lets compliant receivers dedupe too.
**Consequences:** Response body excerpt (first 4 KB) + status code + extracted external ID stored per attempt in `delivery_attempts`.

### D16. Reviewer locking
**Decision:** Soft lock columns on `records` (`locked_by_id`, `lock_token`, `lock_expires_at`): acquired when a reviewer opens a record (TTL 300 s), renewed by heartbeat every 60 s while the editor is open, released on navigation/close, force-releasable by admins, auto-expired by comparison at read time (no cleanup dependency; a cleanup job also clears them for hygiene). All writes additionally require optimistic `version` match. Lock does not block reading.
**Rationale:** Assignment already gives ownership; the lock only guards against the same account in two tabs and admin/reviewer overlap. Optimistic version is the true safety net.
**Consequences:** Save with stale version → `409 Conflict` with server state returned; UI shows a conflict panel (keep mine / take server / merge per field). No silent overwrite anywhere.

### D17. Offline draft synchronization
**Decision:** IndexedDB store `drafts` keyed by `recordId`, storing `{draftOutput, baseVersion, baseExtractionId, savedAt}`. While offline, edits save locally only for records already loaded. On reconnect, the sync engine PUTs each draft with `baseVersion`; server accepts if `records.version == baseVersion` (then normal draft save), else returns `409` with current server state → conflict UI (local vs server side-by-side per field; reviewer resolves manually). Approve/Reject are **never** queued offline — they require a live connection.
**Rationale:** Drafts are low-risk to sync; decisions are not. Version-gated sync prevents both duplicates and silent overwrites.
**Consequences:** Offline banner + per-record "pending sync" badge; logout blocked while unsynced drafts exist (or explicit discard).

### D18. Human edit rate calculation
**Decision:** Computed once, transactionally, at approve time: normalized field-by-field diff (`final_output` vs latest `parsed_output`; normalization = trim, number/date canonicalization, array order-sensitive) stored in `record_reviews.field_changes` as `{changedFields: [keys], addedFields: [], clearedFields: []}`. Aggregations (project/field/reviewer/day) are SQL queries over this JSONB (GIN-indexed) — no separate stats tables in MVP.
**Rationale:** Diff-at-approve is cheap, immutable, and exactly matches "which field did a human change".
**Consequences:** Record-level edit rate = records with ≥1 changed field / approved records; field-level = per-key frequency. Both exposed in dashboards and export columns.

### D19. Confidence usage
**Decision:** Confidence is requested from the model per field in the structured envelope, stored in `field_meta`, displayed as a 3-band indicator (high ≥ 0.8, medium ≥ 0.5, low < 0.5 — thresholds configurable per project), used for: review policy routing (R9), assignment filters ("assign low-confidence"), reprocessing scopes, and dashboard low-confidence rate. Never used to auto-approve in MVP and never labelled "accuracy". Missing confidence renders as "n/a" and is treated as low for routing.
**Rationale:** Honest treatment of a heuristic signal, per brief.
**Consequences:** UI copy fixed: tooltip "Model-reported confidence — a heuristic signal, not measured accuracy."

### D20. Manual review rules
**Decision:** Per-project `review_policy` JSONB: `{mode: "ReviewAll" | "BelowConfidenceThreshold", threshold: 0.8, alwaysReviewFields: [keys], samplingPercent: 0}`. MVP implements `ReviewAll` (default) and `BelowConfidenceThreshold` (records at/above threshold on **all** fields, with zero validation warnings and no `alwaysReviewFields` present, skip review: `review_status` jumps to `Approved` with `approved_by = null`, flagged `autoApproved: true`). Sampling and warning-based modes: Phase 1.1.
**Rationale:** Brief demands auto-approve exist but default off; this is the minimal honest version.
**Consequences:** Auto-approved records are visibly badged and countable; they still pass server-side validation gates.

### D21. Provider abstraction
**Decision:** `IAiCompletionProvider` interface (in `Providers.Contracts`): `Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken ct)` where the request carries messages, model, generation settings, and an optional `JsonSchemaConstraint`. MVP ships **one adapter**: `OpenAiCompatibleProvider` (covers OpenRouter, NVIDIA NIM/AI endpoints, and any custom OpenAI-compatible base URL) with provider-type-specific request decoration (e.g. OpenRouter `provider.require_parameters`, attribution headers). Adapter registry keyed by `ProviderType` enum; Anthropic/Gemini/AzureOpenAI/Ollama are future adapters behind the same interface. Rate limiting (RPM/TPM/concurrency from provider config) enforced by a per-provider `RateGate` (token-bucket via `System.Threading.RateLimiting` + `SemaphoreSlim`).
**Rationale:** All three MVP targets speak the OpenAI chat-completions dialect; one well-tested adapter beats three near-copies. Fallback/routing needs only a policy layer above the interface later.
**Consequences:** `AiCompletionResult` normalizes: content, finish reason, prompt/completion tokens, model echo, raw JSON (for retention), HTTP metadata. Structured-output capability is a per-model config flag; when off, the pipeline uses prompt-embedded schema + strict parsing.

### D22. Dynamic form rendering
**Decision:** A recursive Vue renderer: `DynamicForm` → `FieldRenderer` resolving a component from a registry `Record<FieldType, Component>` (`ShortTextField`, `LongTextField`, `IntegerField`, `DecimalField`, `BooleanField`, `DateField`, `DateTimeField`, `SingleSelectField`, `MultiSelectField`, `EmailField`, `PhoneField`, `UrlField`, `TagsField`, `StringListField`, `ObjectListField`, `ObjectField`, `KeyValueField`, `JsonField`). Nested objects recurse; object lists render as repeatable cards (add/remove/reorder). Validation is interpreted from the field spec by a TS engine mirroring the C# `SchemaOutputValidator` (both interpret the same JSON rules; a shared test-vector file in the repo keeps them equivalent). All value inputs use `dir="auto"`.
**Rationale:** Registry + recursion is the only structure that stays sane with 18 field types and nesting; interpreter-over-shared-spec avoids double-maintained validation logic drift.
**Consequences:** `getDefaultValue(fieldSpec)` and `normalizeValue(fieldSpec, value)` utilities shared by renderer, diff, and draft engine.

### D23. Array and nested object UX + export behavior
**Decision (UX):** Focus Mode: nested object = indented group card; object list = repeatable cards with index headers, add/remove/reorder, collapse. Table Mode: nested/array cells show a summary chip (`3 items`, `{…}`); clicking opens a right-side drawer with the full sub-form; no inline editing of complex types in table cells.
**Decision (Export, deterministic default):** nested object → dot-notation flattened columns (`location.city`); primitive array → single cell joined with `"; "`; array of objects → **child rows in a separate sheet** named after the field key, columns = `Record ID`, `Item Index`, flattened item fields; per-field override `exportMode: "json"` puts raw JSON in one cell. Main sheet always has one row per record.
**Rationale:** Analysts get pivot-ready child sheets; the main sheet stays rectangular; JSON stays available as an opt-in escape hatch.
**Consequences:** Export column picker groups child-sheet fields separately; formula-injection sanitization applies to every written cell.

### D24. Telegram Mini App reuse
**Decision:** The Mini App **is** the Reviewer SPA. Entry route `/tg` (served by the same host): loads the Telegram WebApp JS SDK, exchanges `Telegram.WebApp.initData` for a normal JWT via `POST /api/v1/auth/telegram-miniapp`, maps Telegram theme params onto the design-system CSS variables, uses Telegram BackButton for navigation, then renders the identical reviewer routes/components. PWA-specific features (install prompt, SW) are disabled inside Telegram.
**Rationale:** Literal single codebase; the only Telegram-specific code is the auth/theme/navigation adapter (~300 lines).
**Consequences:** Mini App requires linked account (`telegram_links` row); unlinked users see a linking instruction screen with a deep link back to the bot.

### D25. Deployment architecture
**Decision:** Docker Compose, single server: `app` (one container: ASP.NET Core serving API + SignalR + Hangfire server + built SPA), `postgres` (with named volume), `caddy` (reverse proxy, automatic HTTPS; ships with config — can be swapped for an existing nginx). Migrations applied at app startup under a Postgres advisory lock. First admin bootstrapped from env vars on first run. Persistent volumes: `pgdata`, `appdata` (`/data`: uploads, exports, data-protection keys). Scale-out later = move DataProtection keys + SignalR backplane to Redis and run N app replicas; documented, not built.
**Rationale:** Matches the user-confirmed single-server target with the simplest reliable operation story.
**Consequences:** One `.env` file drives all secrets/config; no secret in the repo; backup = `pg_dump` + `/data` (scripted).

---

## Cross-cutting conventions (binding)

- **IDs:** UUID v7 everywhere (`Guid.CreateVersion7()`); DB type `uuid`.
- **Money:** `numeric(12,6)` USD.
- **Enums:** stored as `text` (string enums) — readable, migration-safe.
- **Timestamps:** `timestamptz`, UTC, `created_at`/`updated_at` on every table (EF interceptor).
- **Optimistic concurrency:** integer `version` column with EF `IsConcurrencyToken` on `records`, `record_reviews`, `schema_versions`, `prompt_versions`, `ai_providers`, `input_connectors`, `output_connectors`, `projects`.
- **Soft delete:** only `projects.archived_at` and `users.deactivated_at`. Everything else uses status fields; no global query-filter soft-delete regime.
- **Secrets at rest:** ASP.NET Data Protection (`ISecretProtector` wrapper, purpose-scoped) with key ring persisted to `/data/keys`; encrypted values stored as `protected:v1:<base64>`; masked in every API response as `••••` + last 4 chars.
- **Audit:** every state-changing handler appends an `audit_events` row in the same transaction via `IAuditWriter`.
- **Correlation:** `X-Correlation-Id` accepted or generated per request; flows into logs, audit, background jobs (carried in job payloads), and SignalR messages.
