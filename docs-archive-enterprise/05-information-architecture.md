# 05 — Information Architecture

## Global structure

```
/signin                      Sign In (+ forced Change Password)
/admin/…                     Admin area  (AdminLayout: left sidebar + topbar)
/review/…                    Reviewer area (ReviewerLayout: minimal top nav / bottom tabs on mobile)
/tg/…                        Telegram Mini App entry → Reviewer area with Telegram adapter
```

Users holding only Reviewer memberships land on `/review`; everyone else lands on `/admin/dashboard`. Users with both see an area switcher in the profile menu.

## Admin navigation (left sidebar)

```
Dashboard                    /admin/dashboard
Projects                     /admin/projects
  → Project workspace        /admin/projects/{id}/…   (see below)
Review Operations            /admin/review-ops              (cross-project)
Reviewers                    /admin/reviewers               (reviewer directory + performance)
Imports                      /admin/imports                 (cross-project import runs)
Exports                      /admin/exports                 (cross-project export runs)
AI Providers                 /admin/providers
Usage & Costs                /admin/usage
Audit Log                    /admin/audit
Users                        /admin/users
Roles                        /admin/roles
Settings                     /admin/settings
```

Notes: "Records / Processing / Assignments / Connectors" from the brief's flat list live **inside the project workspace** — records and processing are meaningless outside a project. The cross-project pages (Review Operations, Imports, Exports) aggregate with a project column + filter.

## Project workspace navigation (secondary sidebar inside a project)

```
Overview                     /admin/projects/{id}/overview
Records                      /admin/projects/{id}/records
  Record Details             /admin/projects/{id}/records/{recordId}
Schema                       /admin/projects/{id}/schema           (builder on Draft)
  Schema Versions            /admin/projects/{id}/schema/versions
AI Configuration             /admin/projects/{id}/ai               (provider/model/generation)
Prompt Configuration         /admin/projects/{id}/prompt
  Prompt Versions            /admin/projects/{id}/prompt/versions
Playground                   /admin/projects/{id}/playground
Test Cases                   /admin/projects/{id}/test-cases
Input Sources                /admin/projects/{id}/inputs           (tabs: Excel Import | Manual | API Connectors)
Processing                   /admin/projects/{id}/processing       (runs list)
  Run Details                /admin/projects/{id}/processing/{runId}
Assignments                  /admin/projects/{id}/assignments      (assignment manager)
Review Queue                 /admin/projects/{id}/review-queue     (admin view of review states)
Output Destinations          /admin/projects/{id}/outputs          (tabs: Excel Export | API Connectors | Deliveries)
Members                      /admin/projects/{id}/members
Usage                        /admin/projects/{id}/usage
Audit                        /admin/projects/{id}/audit
Settings                     /admin/projects/{id}/settings
```

## Reviewer navigation (top nav desktop / bottom tabs mobile — max 5 items)

```
My Tasks                     /review/tasks            (project cards with pending counts)
Review                       /review/queue/{projectId}     (list + Focus/Table toggle)
  Focus Review               /review/queue/{projectId}/focus/{recordId}
  Table Review               /review/queue/{projectId}/table
Completed                    /review/completed
Progress                     /review/progress
Settings                     /review/settings          (profile, password, Telegram linking, theme)
Notifications                bell icon → /review/notifications (badge count; not a tab)
```

## Settings hierarchy

**Global Settings** (`/admin/settings`, `system.settings.manage`):
- General: installation name, base URL (used for Telegram links), default timezone display
- Security: session lifetimes, password policy, rate limits, `ALLOW_INSECURE_HTTP` display (env-driven, read-only)
- Network: global outbound proxy (URL, type, credentials), egress allowlist/denylist
- Telegram: bot token (encrypted), mode webhook/polling, webhook secret, Set Webhook button, bot status check
- Data Retention: raw AI response days, export file days, notification days
- Appearance defaults: default theme

**Project Settings** (`/admin/projects/{id}/settings`, `project.settings.manage`):
- General: name, description, archive project
- Review Policy: mode, confidence threshold, alwaysReviewFields, `allowBulkApprove`
- Processing Defaults: concurrency, retry policy, confirmation threshold
- Budgets: project total / daily / monthly / per-run caps, warning threshold %
- Danger Zone: archive, delete never-processed records

**User Settings** (profile menu / `/review/settings`):
- Profile: name, password change
- Telegram Linking: status, generate code, unlink
- Appearance: Light / Dark / System
- Notifications: toggle Telegram notifications per event type

## Breadcrumbs & context

- Admin area: breadcrumb `Projects / {Project name} / {Page}`. Project switcher dropdown in the project sidebar header.
- Every list page keeps filter state in the URL query string (shareable/bookmarkable).
- Real-time badges: sidebar shows live counts on Processing (running runs) and Review Operations (backlog) via SignalR.
