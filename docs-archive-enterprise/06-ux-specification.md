# 06 — UX Specification

## Design System (binding)

Inspiration: Linear (density + keyboard), Retool/Airtable (data grids), Stripe Dashboard (clarity). No template packages.

**Tokens** (CSS variables in `design-system/tokens.css`, light + dark values; Telegram theme maps onto the same variables):
- Typography: Inter (UI) + JetBrains Mono (code/JSON); sizes 12/13/14/16/20/24; weights 400/500/600. Persian text renders via `font-family` fallback chain incl. `Vazirmatn` (bundled, subset) applied through `:lang(fa)`/`dir` heuristics — record content only.
- Spacing scale: 4/8/12/16/24/32/48. Radius: 6 (inputs/buttons), 10 (cards). Border 1px `--border`.
- Color roles: `--bg`, `--surface`, `--surface-raised`, `--border`, `--text`, `--text-muted`, `--primary` (indigo 600), `--danger`, `--warning`, `--success`, `--info`. Dark theme = same roles, recalibrated. WCAG AA contrast enforced.
- Status badge colors: Imported=gray, Queued=blue, Processing=blue-pulse, Processed=teal, ValidationFailed=amber, ProcessingFailed=red, NeedsReprocessing=purple, Unassigned=gray, Assigned=blue, InReview=indigo, DraftSaved=cyan, Approved=green, Rejected=red, ReturnedForReprocessing=purple, ReadyForExport=teal, Exported=green, DeliveryFailed=red, DeadLettered=dark-red.

**Core components** (all in `design-system/`): Button (primary/secondary/ghost/danger; loading state), Input, Textarea, Select, Combobox, Checkbox, Switch, DatePicker, Tabs, Card, DataTable (sortable, column-config, sticky header, row selection, keyset pagination), Dialog, ConfirmDialog (danger variant requires typed confirmation), Drawer (right side), Toast (4 variants, queue), Tooltip, Badge/StatusBadge, ProgressBar, Skeleton, EmptyState (icon + title + description + action), Banner, Kbd, CodeBlock (JSON highlight, copy), DiffView (side-by-side values), Stepper (wizard), FileDropzone, SearchInput, Pagination, Breadcrumb, Avatar, MetricCard, Sparkline.

**Interaction rules**: every mutation gives feedback ≤200 ms (optimistic or spinner); destructive actions always confirm; all lists have empty/loading (skeleton)/error (retry button) states; keyboard: `⌘K` command palette (nav + actions), Focus Review shortcuts (`A` approve, `R` reject, `E` first field, `←/→` prev/next, `S` save draft); forms validate on blur + on submit; errors from API render field-level when `problem.errors` maps, else toast; autosave indicators ("Saved · 12:03"). Accessibility: full keyboard operability, focus rings, `aria-live` for async results, labels on all inputs, color never sole signal (icons + text on badges).

**Responsive breakpoints**: sm 640 / md 768 / lg 1024 / xl 1280. Admin optimized ≥1024 (usable ≥768: sidebar collapses to icons, tables scroll horizontally with pinned first column). Reviewer fully mobile-first (Mini App ≈ 390 px).

---

## Page specs — template

Each page: **Purpose / Layout / Main sections / Primary actions / Secondary actions / States (empty·loading·error) / Validation / Mobile / Permission**.

### Shared pages

**Sign In** (`/signin`)
- Purpose: authenticate. Layout: centered card, product mark.
- Sections: email, password, sign-in button; error banner. Forced Change Password variant when `must_change_password`.
- Primary: Sign In. Secondary: theme toggle. States: loading on submit; error `invalid_credentials` banner (no user enumeration); lockout message after rate limit. Validation: required fields, email format. Mobile: full-width card. Permission: anonymous.

---

## Admin pages

**Dashboard** (`/admin/dashboard`) — `project.view` (aggregates permitted projects)
- Purpose: operational overview across projects.
- Layout: metric grid (4-up) → two charts → two tables. Sections: record funnel metrics (Total/Imported/Queued/Processing/Processed/Failed/ValidationFailed), review metrics (ReadyForReview/Unassigned/Assigned/InReview/Approved/Rejected), delivery metrics (ReadyForExport/Exported), cost metrics (tokens, est./actual cost, human edit rate, low-confidence rate, avg processing & review time); charts: processing volume by day, cost by provider/model; tables: recent processing runs, recent exports; provider health strip.
- Primary: — (navigational). Secondary: project filter, date range. Empty: "No projects yet" + New Project CTA. Loading: metric skeletons. Error: per-widget retry. Mobile: single-column stack.

**Projects** (`/admin/projects`) — `project.view`
- Purpose: list/find projects. Layout: toolbar (search, status filter) + card grid or table toggle.
- Card: name, description, status (Draft/Active/Archived), record count, pending review count, last activity. Primary: New Project (`projects.create`). Secondary: archive filter. Empty: illustration + "Create your first project". Mobile: cards single-column.

**Create Project Wizard** (`/admin/projects/new`) — `projects.create`
- Purpose: guided setup, 5 steps (R10). Layout: Stepper top, content, sticky footer (Back / Save & Continue Later / Continue).
- Steps: 1 Details (name, description); 2 Output Schema (embedded Schema Builder); 3 AI & Instructions (provider, model, generation settings; prompt fields — compact); 4 Test with Sample (embedded mini-Playground; skippable with notice); 5 Review & Create (summary of all config; Create button publishes schema v1 + prompt v1 automatically).
- Validation per step gate; wizard state saved server-side each step (Draft project). Error: step-level banners. Mobile: usable but desktop-recommended notice. Empty/loading: standard.

**Project Overview** (`/admin/projects/{id}/overview`) — `project.view`
- Purpose: project home + setup checklist + funnel.
- Sections: setup checklist card (schema published ✓, AI configured ✓, prompt published ✓, input source added, reviewers added, output configured — each linking to its page; hidden when complete), status funnel (Imported→…→Exported with live counts, each segment links to filtered Records), active runs strip (live progress), budget card (spent vs caps), recent activity (audit excerpt).
- Primary: contextual "Continue setup" or "New Processing Run". States: standard; empty = checklist prominent. Mobile: stacked.

**Records** (`/admin/projects/{id}/records`) — `project.records.view`
- Purpose: browse/filter/operate on all project records.
- Layout: filter bar (search text/ID, processing status, review status, delivery status, reviewer, confidence band, has-validation-errors, import run, date) + DataTable (columns: External ID, text excerpt, 3 status badges, reviewer, confidence min, updated) + right Drawer preview on row click.
- Primary: bulk actions on selection — Send to Processing, Assign, Export selection, Delete (only `Imported`, R22). Secondary: column config, saved filters, CSV of current view. Row actions: open details.
- Empty: "No records yet — import a file or add manually" + CTAs. Loading: table skeleton. Error: retry. Validation: bulk action eligibility per record with per-row skip reasons. Mobile: card list w/ badges. Keyset pagination (50/page).

**Record Details** (`/admin/projects/{id}/records/{recordId}`) — `project.records.view`
- Purpose: full record inspection & history.
- Sections: header (external ID, badges, reviewer, lock indicator + admin Release Lock); tabs: **Data** (original text `dir=auto`, metadata, AI output vs human output side-by-side DiffView with per-field confidence/evidence), **Extractions** (attempt history: run, model, tokens, cost, status, raw response viewer — `system`-permission gated for raw), **Review** (assignment history, review events, notes), **Deliveries** (per-connector delivery rows + attempts), **Audit**.
- Primary: Reprocess (scope=this record), Assign/Reassign. Secondary: Release Lock (`project.records.manage`), Delete (guarded). States: standard. Mobile: tabs stack.

**Schema Builder** (`/admin/projects/{id}/schema`) — `project.schema.manage`
- Purpose: define/edit the Draft schema version.
- Layout: three panes — field tree (left, drag-to-reorder, add field / add template Person·Location), field editor (center: all FieldSpec properties in grouped sections General/Extraction/Validation/Review/Display/Export), live preview (right: tabs JSON Shape | Form Preview).
- Primary: Save Draft, Publish… (diff dialog, F5). Secondary: duplicate field, delete field (confirm if referenced by prompt fieldInstructions), import/export definition JSON. Validation: on save — key uniqueness/format, regex validity, depth/count caps, allowedValues consistency; inline per-field error markers in tree. Empty: "Add your first field" + type menu. Error: standard. Mobile: read-only warning (editing desktop-only ≥1024).

**Schema Versions** (`/admin/projects/{id}/schema/versions`) — `project.schema.manage` (view: `project.view`)
- List: version, status, field count, published by/at, records processed with it. Actions: view (read-only builder), diff vs any version, "Restore as new draft". Never delete published versions.

**AI Configuration** (`/admin/projects/{id}/ai`) — `project.ai.manage`
- Purpose: bind provider/model + generation settings for the project.
- Sections: provider select (enabled providers only, with type badge), model select (from provider's available models + free text), generation overrides (temperature, topP, maxOutputTokens), structured-output capability flag display, concurrency override, per-project request timeout. Test button runs a 1-token ping via chosen model.
- Primary: Save. Validation: provider enabled, model non-empty, numeric ranges. States: warning banner if provider disabled/deleted (project keeps config but runs are blocked with clear error). Mobile: single column.

**Prompt Configuration** (`/admin/projects/{id}/prompt`) — `project.prompts.manage`
- Purpose: edit Draft prompt version. Layout: form left, live compiled-prompt preview right (with sample record).
- Sections: system instruction (textarea), general extraction instruction, input template (placeholder helper + validation of `{{…}}` tokens), context fields (from import mapping keys), output language, missing/ambiguous/unknown behaviors (selects), strict JSON toggle, few-shot examples editor (pairs; expected JSON validated against current schema), reference context. Field-level instructions live in Schema Builder (single source), shown here read-only with links.
- Primary: Save Draft, Publish…. Validation: template placeholders resolvable; few-shot JSON valid. Prompt Versions page mirrors Schema Versions.

**AI Providers** (`/admin/providers`) — `system.providers.manage`
- Purpose: manage global providers + model prices.
- Layout: provider table (name, type, base URL, default model, enabled, last test result) + editor drawer; second tab **Model Prices** (editable grid: provider, model, input/output $ per 1M tokens, updated; "Sync from OpenRouter" button fills known prices).
- Primary: New Provider, Test Connection, Save. Secondary: enable/disable, delete (blocked if referenced by projects — offer disable). Validation: URL https (unless dev flag), key required on create, numeric limits. API key field is write-only (masked, "Replace key" action). States: standard. Mobile: table scrolls.

**Input Sources** (`/admin/projects/{id}/inputs`) — tabs:
- **Excel Import** (`project.imports.run`): dropzone → mapping screen (F9/F10 spec: preview grid 50 rows, column mapping selects, duplicate policy, per-column warning counts) → import progress screen (live counters, cancel, row-error download). Import history table (runs with status/counters/actor). Empty: dropzone hero. Errors: file-level errors as banner with error code (see error matrix).
- **Manual** (`project.records.manage`): single record form (external ID optional + text + metadata key/values) and bulk paste (one record per line or `ID<TAB>Text`; preview parse table before submit).
- **API Connectors** (`project.connectors.manage`): connector list + builder (see below).

**API Input Connector builder** (within Inputs) — `project.connectors.manage`
- Purpose: configure external API ingestion.
- Layout: vertical sections — Connection (name, base URL, endpoint, method, headers, query params, auth type + credentials, timeout, proxy override), Pagination (type: none/page/cursor; page size; cursor path; next-page path), Mapping (data array JSONPath, ID JSONPath, text JSONPath, metadata mappings, incremental sync field + date filter), Schedule (manual / interval cron), Limits (max response size, retry policy).
- Primary: Test Connection → Preview Response (raw JSON) → Preview Mapped Records (table of would-be records with dedup annotation) → Save. Secondary: Run Now, View Sync History (runs with checkpoints/counters), enable/disable. Validation: URL/SSRF pre-check (server), JSONPath syntax, cron syntax. States: test errors render provider-style diagnostics. Mobile: desktop-recommended.

**Processing Runs** (`/admin/projects/{id}/processing`) — `project.processing.run`
- List: run name (auto: "Run #12 — 4,988 records"), scope summary, model, status badge, progress bar, succeeded/failed, cost, started by/at. Primary: New Run (opens scope+estimate dialog, F12). Empty: "No runs yet". 

**Processing Run Details** (`/admin/projects/{id}/processing/{runId}`) — `project.processing.control` for controls
- Sections: header (status, controls Pause/Resume/Cancel/Retry Failed), live progress (bar, counters, throughput, ETA), cost card (tokens + cost vs estimate vs budget), config snapshot viewer (read-only), error breakdown (by error code, expandable), task table (record, status, attempts, duration, error; filter by status; link to record). SignalR-live with polling fallback.
- States: terminal states show summary banner (incl. `StoppedByBudget` explanation). Mobile: stacked, controls sticky.

**Review Operations** (`/admin/review-ops` global + project Review Queue) — `project.assignments.manage`
- Purpose: review backlog health + reviewer performance (F28 spec). Sections: backlog metrics, per-reviewer table (pending, completed today, avg time, edit rate, rejection rate, oldest pending, Telegram linked ✓), fairness disclaimer footnote, unassigned queue quick-assign.
- Primary: New Assignment Batch. Mobile: metric cards stack.

**Assignment Manager** (`/admin/projects/{id}/assignments`) — `project.assignments.manage`
- Layout: batches list (left) + assignment table (right: record, reviewer, status, priority, due, started, completed, duration). New Batch dialog per F17 (filter, reviewers multi-select, strategy, priority, due date, preview distribution).
- Actions: Reassign / Unassign / Change priority / Change due date / Cancel batch remainder / Release locked / Move unfinished (dialog: from reviewer → to reviewer). All bulk-capable with per-row result report. Empty: "No assignments yet".

**Reviewer Management** (`/admin/reviewers`) — `system.users.manage` scope-filtered
- Directory of users with Reviewer memberships: projects, pending load, last active, Telegram status, deactivate. Links to per-reviewer performance view.

**Export Center** (`/admin/projects/{id}/outputs` → Excel tab; global `/admin/exports` aggregates) — `project.exports.run`
- New Export dialog (F30: filter, column picker with drag order, child-sheet groups, format xlsx/csv). Runs table: status, rows, size, duration, download (active 30 days), error detail. Empty state explains prerequisites (approved records needed).

**API Output Connector + Deliveries** (`/admin/projects/{id}/outputs` → API tab) — `project.deliveries.run` / `project.connectors.manage`
- Connector builder per F31 (connection, auth, body template editor with `{{placeholder}}` autocomplete from schema, mapping preview, batch settings, success codes, response ID path, retry policy, create-or-update). Test Request panel (dry run / send sample).
- Deliveries: runs list → run detail (per-record delivery table: status, attempts, last code, external ID; Retry Failed; dead-letter banner).

**Playground** (`/admin/projects/{id}/playground`) — `project.playground.use`
- Layout: left input column (sample text textarea `dir=auto`, context fields, provider/model/version selectors), right results column (tabs: Form Preview / Parsed JSON / Raw Response / Compiled Prompt / Validation; metrics strip: input+output tokens, est. cost, duration). Run button with loading state (streams status: "Calling model…").
- Primary: Run Test, Save as Test Case. States: error tab shows structured diagnostics (provider error, parse failure with repair attempts). Mobile: columns stack.

**Test Cases** (`/admin/projects/{id}/test-cases`) — `project.playground.use`
- Table: name, last run result (pass/fail/error vs expected, or "no expectation"), last model, updated. Actions: Run, Run All (progress + summary), edit, view diff (expected vs actual, last vs previous). Empty: "Save a Playground run as your first test case".

**Usage & Costs** (`/admin/usage` global; `/admin/projects/{id}/usage`) — `system.usage.view` / `project.usage.view`
- Sections: totals (tokens in/out, est. vs actual cost), charts by day/provider/model/project, source split, budget bars, cost per processed & per approved record, table of processing runs with costs, model price table link. Date range filter. Export CSV.

**Audit Log** (`/admin/audit`; project-scoped variant) — `system.audit.view` / `project.audit.view`
- Filterable table (time, actor, category, action, entity, project, correlation ID) + expandable JSON detail (before/after summary). Export CSV. Retention note.

**Users** (`/admin/users`) — `system.users.manage`
- Table (name, email, global role, projects count, Telegram, status, last login). New/Edit drawer (F2), set password (generates or manual, forces change), deactivate/reactivate (guard: last admin), revoke Telegram link, view user's audit trail. Validation: email unique/format, password policy meter.

**Roles** (`/admin/roles`) — `system.roles.manage`
- Seeded roles read-only (permission matrix view); custom roles: name + permission checklist grouped by area (global vs project sections). Delete blocked while assigned.

**Settings** (`/admin/settings`) — `system.settings.manage`
- Grouped panels per IA settings hierarchy (General/Security/Network/Telegram/Data Retention/Appearance). Telegram panel: masked token with Replace, mode select, webhook URL display + Set Webhook button + last webhook check result, bot username display, test message button. Validation: URL formats, numeric ranges. Every save audited.

---

## Reviewer pages

**My Tasks** (`/review/tasks`) — `project.reviews.own`
- Purpose: entry point; what needs doing. Layout: project cards — project name, pending count, due soon count, priority split, progress today; "Start Reviewing" per card. Global summary strip (total pending, completed today).
- Empty: friendly "All caught up 🎉 — no records assigned to you". Loading: card skeletons. Error: retry. Mobile: single column (default target).

**Review Queue** (`/review/queue/{projectId}`) — `project.reviews.own`
- Purpose: browse own assigned records; switch Focus/Table. Layout: toolbar (status filter: Assigned/InReview/DraftSaved; validation filter; confidence filter; sort priority/due/oldest) + list rows (excerpt, badges, confidence min, due) + view toggle.
- Primary: Continue (first actionable record → Focus). States: empty per filter; standard loading/error. Mobile: list rows tap → Focus.

**Focus Review** (`/review/queue/{projectId}/focus/{recordId}`) — `project.reviews.own` (+ assignment ownership server-checked)
- Purpose: the core one-record review surface (see wireframe W11).
- Layout desktop: split — left panel original text (`dir=auto`, sticky, highlight of evidence quote on field focus) + metadata/context accordion; right panel dynamic form (grouped per schema sections, per-field: value input, confidence dot, AI-original popover with revert, validation message inline, evidence quote). Footer action bar (sticky): Save Draft · Return for Reprocessing · Reject · **Approve** · prev/next · position "12 / 100". Progress bar top. Offline banner + pending-sync badge when applicable.
- Validation: live client + authoritative on action; Approve disabled with reason tooltip while required/validation errors exist (list panel expandable). Reject/Return require note dialog. Lock: acquired on open; "read-only — locked by X" banner if lock unavailable; heartbeat while open; unsaved-changes guard on navigation.
- States: loading skeleton (text + form); error with retry; end-of-queue state ("Queue complete" + summary + back to tasks). Mobile/Mini App: stacked (text collapsible accordion on top, form below, action bar bottom, swipe left/right = next/prev). Keyboard shortcuts per Design System.

**Table Review** (`/review/queue/{projectId}/table`) — `project.reviews.own`
- Layout: DataTable of own assigned records; configurable columns (schema scalar fields inline-editable, complex fields chip → Drawer sub-form), validation/confidence indicator columns, multi-select checkbox column.
- Primary: Bulk Approve (guarded per R20; per-row result toast + inline markers), Bulk Reject, Bulk Return (note dialog). Row: open in Focus. Inline edit saves as draft (lock+version per row, transient lock). States: standard. Mobile: falls back to Review Queue list (table hidden < md).

**Completed Tasks** (`/review/completed`) — `project.reviews.own`
- Table/list of own completed reviews (record, decision, project, date, duration, edited fields count). Read-only record view on click. Filter by decision/project/date.

**Progress** (`/review/progress`) — `project.reviews.own`
- Personal stats: completed today/week, avg review time, current streak of queue, per-project split, due-soon list. No comparative ranking (fairness).

**Notifications** (`/review/notifications`)
- List (assignment received, due reminder, record returned info, system notices) with read/unread, mark all read. Badge count in nav via SignalR.

**Reviewer Settings** (`/review/settings`)
- Profile (name, password change), Telegram Account Linking (status card; Generate Code → shows code + deep-link button + QR + expiry countdown; Unlink with confirm), theme, notification toggles.
