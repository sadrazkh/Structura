# 02 — Pages and Core User Flows

## Final page list

### Admin area (`/admin`, left sidebar; roles: Administrator, Project Manager)

| Page | Route | Content |
|---|---|---|
| Login | `/login` | email + password; forced change-password screen when flagged |
| Dashboard | `/admin` | per-project cards: record counts by status, active run progress, quick links |
| Projects | `/admin/projects` | list + New Project (name, description) |
| Project Settings | `/admin/projects/{id}/settings` | name, description, members (add/remove PM & reviewers), archive |
| Schema Builder | `/admin/projects/{id}/schema` | field list (drag order) + field editor (8 types, V1 settings) + form preview; save increments `schema_version` |
| AI Settings | `/admin/projects/{id}/ai` | provider, base URL, API key (masked), model, temperature, max tokens, timeout, Test Connection; prompt: system + extraction instructions |
| Import Records | `/admin/projects/{id}/import` | tabs: Excel/CSV upload → column mapping (ID optional, Text required) → progress; Manual (single form + bulk paste); API (url/method/headers/auth/paths + Fetch Now + preview) |
| Records | `/admin/projects/{id}/records` | filterable table (status ×3, reviewer, search), row detail drawer (text, AI output vs final output, extraction attempts), bulk select → Process / Assign / Delete(pending-only) |
| Processing Runs | `/admin/projects/{id}/runs` | run list + run detail: live progress bar, succeeded/failed counts, token usage, error list, Cancel, Retry Failed |
| Assignments | `/admin/projects/{id}/assignments` | select records (filtered Unassigned) → assign to one reviewer / distribute evenly; per-reviewer pending table; Unassign / Reassign |
| Review Status | `/admin/projects/{id}/review` | counters (per review status), per-reviewer progress (pending / approved / rejected / avg per day), reject notes list |
| Export | `/admin/projects/{id}/export` | Excel download (scope: Approved, optional date filter); API output config (url/method/headers/auth/body template + Test Request) + delivery table (status, attempts, retry failed) |
| Users | `/admin/users` | Administrator only: list, create (name/email/password/role), reset password, deactivate |
| Settings | `/admin/settings` | Administrator only: Telegram bot token (masked), webhook/polling mode, Set Webhook, public base URL |

### Reviewer area (`/review`; bottom tabs on mobile; also served at `/tg` for the Mini App)

| Page | Route | Content |
|---|---|---|
| My Tasks | `/review` | project cards with pending counts + Start Reviewing |
| Record List | `/review/{projectId}/list` | own assigned records: status, excerpt; select → Bulk Approve; tap → Focus |
| Review Record (Focus) | `/review/{projectId}/record/{recordId}` | original text (dir=auto) + dynamic form (AI values, editable) + validation hints + actions: Save, Approve, Reject (note), Reprocess (note), Prev/Next; auto-advance after decision |
| Progress | `/review/progress` | own totals: pending, approved, rejected, today/this week |
| Settings | `/review/settings` | change password, Telegram linking (generate code / unlink), theme |

Every page implements loading / empty / error states. Reviewer UI is mobile-first (~390 px); Admin is desktop-first, usable at ≥768 px.

## Core user flows

**F1 — First run & login:** `.env` bootstrap admin → migrations + seed on startup → admin logs in → forced password change → Dashboard.

**F2 — Project setup:** New Project → Schema Builder: add fields (e.g. firstName, incidentType singleSelect, incidentDate, isUrgent) → save → AI Settings: pick OpenRouter, paste key, model, Test Connection ✓ → write system/extraction instructions → save.

**F3 — Import:** Import → upload `.xlsx` → preview first 20 rows → map ID column (or "generate IDs") + Text column → Start → background import streams rows (duplicates by external ID skipped & counted, empty text rows counted as errors) → progress live → records appear as `Pending / Unassigned / Pending`.

**F4 — Processing:** Records → select all Pending (or Runs → Process All) → confirm dialog (count + model) → run created with schema+prompt snapshot → worker processes with concurrency N → live progress (SignalR) → per record: `Completed` + extraction result, or `Failed` + error. Retry Failed re-queues only failed ones into a new run. Cancel stops dispatching; in-flight finish. App restart mid-run: worker picks up where the DB says it left off.

**F5 — Assignment:** Assignments → filter Completed+Unassigned → select 200 → "Distribute evenly" among reviewers A, B → 100 each, records `Assigned` → Telegram notification per linked reviewer ("100 new records in *Project*", button opens Mini App).

**F6 — Review:** Reviewer opens PWA or Mini App → My Tasks → Start → Focus shows record 1/100: original text left/top, form right/bottom prefilled with AI values → fixes a wrong date → Approve → auto-advance. Reject and Reprocess require a note. Record List → select 10 clean records → Bulk Approve (server re-checks: required fields filled, allowedValues valid). Approved records: `final_output` frozen, `delivery_status = Pending` (if API output configured).

**F7 — Reprocess loop:** ReprocessRequested records appear in admin Records filter → included in next "Process selected/all reprocess-requested" → on success return to the same reviewer as `Assigned`.

**F8 — Export & delivery:** Export → download Excel (streams: Record ID, each schema field as a column, Review Status, Reviewer, Review Date) → sanitized against formula injection. If API output configured: delivery worker POSTs each approved record's rendered body template → `Delivered` or `Failed` (+error, attempts) → Retry Failed button re-queues.

**F9 — Telegram link:** Reviewer Settings → Generate Code (8 chars, 10 min TTL) → opens bot, sends `/start CODE` → linked → notifications active. `/tasks` shows remaining counts; `/next` deep-links to next assigned record in the Mini App.
