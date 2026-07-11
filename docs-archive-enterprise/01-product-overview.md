# 01 — Product Overview

## A. Executive Product Summary

### Problem statement

Organizations hold large volumes of unstructured text (incident reports, case notes, support tickets, legal summaries, field reports). Extracting structured data from that text is today done manually: slow, expensive, inconsistent, and unauditable. Pure-AI extraction is fast but unreliable enough that no serious organization will feed it into downstream systems unverified.

Structura closes that gap with a configurable pipeline: **an admin defines the target structure and instructions, AI performs bulk extraction, humans review and correct, and the system delivers verified structured data** to Excel or external APIs — with full versioning, auditability, and cost control.

### Target users

| User | Description |
|---|---|
| System Administrator | IT/ops owner of the installation; manages users, roles, providers, global settings |
| Project Administrator | Domain owner; configures schema, prompts, AI settings for their projects |
| Operations Manager | Runs imports, processing batches, assignments, exports for permitted projects |
| Reviewer | Verifies and corrects AI-extracted records via PWA or Telegram Mini App |
| Auditor | Read-only oversight: audit log, usage, quality metrics |

### Main use cases

1. Bulk extraction of structured fields from thousands of free-text reports imported from Excel.
2. Continuous extraction from an external system via a scheduled API input connector.
3. Human verification workforce management: assignment, progress tracking, throughput and edit-rate metrics.
4. Verified data delivery: curated Excel exports and idempotent pushes to external APIs.
5. Prompt/schema iteration with a Playground and saved test cases before spending money on bulk runs.

### Value proposition

- **Trustworthy output**: every value traceable to AI origin or human edit, with full audit.
- **No code per use case**: schema, prompts, providers, connectors are all admin-configured.
- **Operational control**: durable background processing with pause/resume/retry, budgets, and cost visibility.
- **Reviewer efficiency**: purpose-built Focus/Table review modes, Telegram notifications, offline-tolerant PWA.

### Product boundaries

- Structura extracts structure from **text**. OCR, audio, and image ingestion are out of scope (text must arrive as text).
- Structura is not a BI tool; dashboards cover operations/quality, not analytics on extracted content.
- Structura is not an annotation tool for ML training data (though exports could be used that way).
- MVP is a **single-organization, self-hosted** installation. Multi-tenancy is architecture-ready but not built.

### Non-goals (MVP)

- Multi-tenancy / workspaces (future; see [18-mvp-roadmap.md](18-mvp-roadmap.md))
- Double review, QA sampling, adjudication workflows (state machine reserves states; logic is future)
- Model routing/fallback automation (abstraction supports it; automation is future)
- Google Sheets / DB / SFTP / MQ / webhook connectors (interfaces support them; only Excel + REST API in MVP)
- Fine-tuning, embeddings, RAG
- Public self-registration; email delivery infrastructure (SMTP) — admins create users and set initial passwords

## B. Clarified Requirements — Ambiguities, Contradictions, Resolutions

All resolutions below are **binding** for implementation. Items marked (user-confirmed) were answered by the product owner; the rest are documented assumptions.

| # | Topic | Resolution |
|---|---|---|
| R1 | Input text language | Persian **and** English (user-confirmed). Original text and all dynamic form values render with `dir="auto"`; per-project `outputLanguage` setting controls extraction output language. UI chrome stays English/LTR. |
| R2 | Outbound network | Optional proxy support (user-confirmed). Global default proxy + per-AI-provider and Telegram proxy override (HTTP/SOCKS5). Empty = direct. |
| R3 | Scale target | ≤ ~50,000 records per import, ≤ ~20 concurrently active reviewers, low-millions records total lifetime (user-confirmed). Design uses keyset pagination and streaming import/export but no table partitioning. |
| R4 | Deployment | Single-server Docker Compose (user-confirmed). Architecture keeps scale-out possible (stateless app, Postgres-backed jobs, SignalR single-node; Redis backplane is a documented future step). |
| R5 | Tenancy | Single organization per installation. `Project` is the isolation unit. All project-scoped tables carry `project_id` to make a future `workspace_id` addition mechanical. |
| R6 | User provisioning | No self-registration, no SMTP. Admin creates users with an initial password; `must_change_password` forces change at first login. Password reset is admin-performed. |
| R7 | "Enum" vs "Single Select" field types | Merged: one `singleSelect` type with `allowedValues`. `Person` and `Location` are composite **templates** that expand into predefined nested-object structures (editable after insertion), not primitive types. |
| R8 | Confidence | Model-self-reported per field (0–1) via the structured output envelope. Explicitly documented in UI as a heuristic signal, never as accuracy. Used only for filtering/routing/review policy. |
| R9 | Auto-approve | Off by default. MVP ships `ReviewAll` (default) and `BelowConfidenceThreshold` review policies; warning-based, field-based and sampling policies are Phase 1.1 (schema supports them from day one). |
| R10 | Wizard length | Reduced from 9 to 5 steps: Project Details → Output Schema → AI & Instructions → Test with Sample (skippable) → Review & Create. Input sources, review workflow and outputs are configured post-creation via a Project Overview setup checklist. Wizard progress is saved (project in `Draft` status). |
| R11 | Nested/array export | Deterministic default: nested objects → flattened dot-notation columns; primitive arrays → `"; "`-joined cell; object arrays → child rows on a separate sheet keyed by `Record ID`; per-field override to `JSON in cell`. See decision D23. |
| R12 | Raw AI response retention | Kept 90 days by default (configurable per installation, 7–365 days or forever). Parsed output kept forever. Cleanup job nulls `raw_response` past retention. |
| R13 | Reprocessing approved records | Excluded from processing scopes by default. Requires explicit `includeApproved: true` + a confirmation phrase in UI; resets review state, archives the previous approved output into the review history, writes an audit event. |
| R14 | Reviewer sees confidence/evidence | Yes when present; both are optional in the envelope. Evidence = short source-text quote per field, model-generated. |
| R15 | Telegram delivery in restricted networks | Bot supports both webhook mode and long-polling mode (config flag) so installations behind proxies/NAT still work. Mini App requires a public HTTPS origin. |
| R16 | Cost figures | Estimated from an editable `model_prices` table (USD per 1M input/output tokens). Actual token counts come from provider responses. No hard-coded prices. |
| R17 | Timezones/dates | Store UTC. Schema `date`/`dateTime` values stored as ISO 8601 strings inside JSONB outputs. |
| R18 | Concurrency safety | Optimistic concurrency (integer `version` column) on `records`, plus soft locks (TTL 300s, heartbeat 60s) for review editing. Backend enforces both. |
| R19 | Review after return-for-reprocessing | If the returning reviewer still has an active assignment, the reprocessed record goes back to them (`Assigned`); otherwise `Unassigned`. |
| R20 | Bulk approve safeguards | Server-enforced: project flag `allowBulkApprove`, zero validation errors, all required fields present, record not locked by another user, `review_status ∈ {Assigned, InReview, DraftSaved}`. Each record approved individually inside the batch (partial success reported). |
| R21 | Single Vue app | One Vue 3 SPA hosts both Admin area (`/admin`) and Reviewer area (`/review`), code-split. Telegram Mini App loads the same Reviewer area via `/tg` entry with a Telegram auth adapter. Satisfies the shared-codebase constraint. |
| R22 | Record deletion | Records are hard-deletable only while `processing_status = Imported` and never after any review activity; otherwise admins use `Cancelled`/exclusion. Projects are archived (soft), never hard-deleted from UI. |
| R23 | Input connector scheduling | MVP: manual run + interval schedule (cron expression). Webhook input is future. |
| R24 | Export files | Written to a persistent volume (`/data/exports`), downloadable via authenticated endpoint, retained 30 days (configurable), then cleaned up. |
| R25 | API versioning | All endpoints under `/api/v1`. Breaking changes require `/api/v2`; none planned in MVP. |
| R26 | Language of AI instructions | Admins may write prompt instructions in any language; the platform's own scaffolding prompt text is English. |
| R27 | Budget enforcement | Checked before each record task starts (estimated) and after each response (actual). Run stops with status `StoppedByBudget` when exceeded; in-flight requests complete. |
| R28 | Notifications | In-app notification center + Telegram push for linked users. No email. |

### Contradictions found in the source brief (and how they are resolved)

1. Brief lists both `Enum` and `Single Select` field types → merged (R7).
2. Brief demands "Reviewer PWA and Mini App share one codebase" and also "Admin Panel and Reviewer Application" as two environments → resolved with one SPA, two route areas (R21); one deployable unit as required.
3. Brief asks for a 9-step wizard and also "analyze and reduce steps if needed" → reduced to 5 (R10).
4. Brief requires offline review but forbids duplicate/overwriting writes → resolved with version-checked draft sync + explicit conflict resolution UI (decision D17).
5. Brief requires "process all records" and "approved records must not be accidentally overwritten" → default scopes exclude approved records (R13).
