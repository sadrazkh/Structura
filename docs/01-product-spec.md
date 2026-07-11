# 01 — Simplified Product Specification

## What it does

1. Admin creates a **Project**, defines its output fields dynamically, configures one AI provider (OpenRouter or NVIDIA OpenAI-compatible) and the extraction prompt.
2. Admin imports records (Excel/CSV with ID + Text columns, manual entry, or a basic API fetch).
3. Admin sends records to AI (selected or all). Processing runs in the background with progress, retry-failed, cancel, and restart-resume.
4. Admin assigns processed records to reviewers (to one reviewer, or distributed evenly).
5. Reviewers open records one by one (PWA or Telegram Mini App), see the original text and extracted fields, edit, then Approve / Reject / Request Reprocess. Simple bulk approve exists in Table Mode.
6. Admin exports approved records to Excel and, if configured, delivers them to one external API (with retry for failures).
7. Telegram bot notifies reviewers of new assignments and remaining counts, and opens the Mini App / next record. No forms inside chat.

## Roles (fixed — no custom roles, no permission builder)

| Role | Can |
|---|---|
| **Administrator** | Everything: users, all projects, global settings (Telegram bot token) |
| **Project Manager** | Create projects; full control of projects they are a member of (schema, AI settings, import, processing, assignment, export, delivery). No user management, no global settings |
| **Reviewer** | Only: see and review records assigned to them in projects they are a member of |

Rules enforced in backend with simple checks: role check + project membership check + (for reviewers) `assigned_reviewer_id = current user` in queries. Admins create users and set initial passwords (`must_change_password` forces change at first login). No self-registration, no email.

## Dynamic fields (V1 set)

Types: `shortText, longText, integer, decimal, boolean, date, singleSelect, multiSelect`.
Per-field settings: `key` (camelCase, unique), `label`, `type`, `required`, `description`, `extractionInstruction`, `allowedValues` (for selects), `defaultValue`, `displayOrder`.
No nested objects, no arrays of objects, no custom JSON, no per-field validation rules/regex/confidence in V1.

**Schema versioning (simple):** the project stores its fields as one JSONB document plus an integer `schema_version` that increments on every saved change. Each processing run snapshots the fields JSON at start, and the reviewer form renders from the run snapshot referenced by the record's latest extraction — so mid-run schema edits never corrupt old results. No draft/publish workflow.

## AI configuration (per project)

`provider` (`OpenRouter` | `Nvidia`), `baseUrl` (prefilled per provider, editable), `apiKey` (encrypted at rest, masked in UI), `model` (text), `temperature`, `maxOutputTokens`, `timeoutSeconds`, plus a **Test Connection** button. One prompt config per project: `systemInstruction`, `extractionInstruction` (field-level instructions live on the fields). No versioning, no routing, no fallback, no budgets. Token usage per run is recorded and displayed (counts only — no cost engine).

## Statuses (three independent columns on `records`)

- **Processing:** `Pending → Processing → Completed | Failed`; `Failed → Pending` (retry); `Completed → Pending` (reprocess).
- **Review:** `Unassigned → Assigned → InReview → Approved | Rejected | ReprocessRequested`; `Assigned/InReview ⇄` on open; `ReprocessRequested → Assigned` after successful reprocess (same reviewer kept); `Rejected → Assigned` (reassign); unassign returns to `Unassigned`. Records are `Unassigned` only after processing `Completed`.
- **Delivery:** `Pending → Delivered | Failed`; `Failed → Pending` (retry). Applies only to approved records when an API output is configured; Excel export does not change delivery status.

No separate state-machine framework — one static guard class per status (`EnsureTransition(from, to)`) called by every write path; invalid transition → HTTP 409 `invalid_state`.

## Data separation (kept from the original principle)

- `records.text` — original input, never modified.
- `extraction_results` — one row per AI attempt: raw response + parsed `output` JSONB (AI's values, never edited by humans).
- `records.final_output` JSONB — the reviewer's working/approved copy, initialized from AI output when review starts. Human edits never overwrite AI results.

## Fixed decisions & assumptions

| # | Decision |
|---|---|
| D1 | Single web project (`Structura.Web`) with feature folders; Vue SPA in `ClientApp/`, built into `wwwroot`. One deployable container + PostgreSQL. |
| D2 | Background processing = **custom hosted worker over DB state** (no Hangfire/queue broker): the DB is the queue; restart-resume is automatic. See doc 05. |
| D3 | Excel export is a **synchronous streaming download** (MiniExcel) — no export-run table or job. Import and AI processing and API delivery are background. |
| D4 | No record locking system: one reviewer per record + optimistic `version` column on records is enough. Concurrent save → 409 with server state; UI lets the user reload or overwrite explicitly. |
| D5 | Offline-first sync is out of scope. PWA = installable, fast, app-like; requires connectivity for actions. |
| D6 | No audit tables; structured Serilog logs with user/correlation context cover V1 traceability. |
| D7 | Input text languages: Persian + English → `dir="auto"` everywhere record content renders; UI chrome stays English/LTR. |
| D8 | Optional outbound proxy (global env var) applies to AI providers and Telegram — needed in restricted networks. |
| D9 | Scale target: ≤ 50k records per project, streaming import/export, keyset pagination on records. |
| D10 | Validation of AI output: type check + required + allowedValues only. Validation failure ⇒ record `Failed` with error detail (no separate ValidationFailed status). |
| D11 | Raw AI responses are kept indefinitely in V1 (no retention job). |
| D12 | API input = one manual "Fetch now" pull (no scheduling/pagination beyond a single optional `dataPath` array). API output = one destination per project. |

## Out of scope (V1) — do not design or implement

Multi-tenancy, workspaces, custom roles/permission builder, double review/QA/adjudication, nested schemas/arrays, plugin system, multiple output destinations, analytics, billing/cost budgeting, scheduling, OAuth/webhook connectors, model routing/fallback, offline sync, enterprise audit, workflow engine, microservices, Hangfire, Redis.
