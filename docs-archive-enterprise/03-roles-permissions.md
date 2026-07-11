# 03 — Roles and Permission Matrix

## Model

- **Permissions** are static code constants (class `Permissions` in `Structura.Modules.Identity.Contracts`). They are not editable rows; the DB stores role→permission mappings only.
- **Roles** are DB rows. Five system roles are seeded and non-deletable; admins may create custom roles from the same permission set.
- **Scoping:** a role assignment is either **global** (row in `user_roles`) or **project-scoped** (row in `project_members` with a role). Project-scoped permissions apply only inside that project. Global roles apply everywhere.
- **Enforcement:** every endpoint declares one required permission. Authorization handler resolves: global roles → allowed everywhere; project routes (`/projects/{projectId}/…`) additionally check `project_members`. UI hides what the user cannot do, but the backend check is authoritative.

## Permission constants

Global scope:

| Key | Meaning |
|---|---|
| `system.users.manage` | Create/edit/deactivate users, set passwords |
| `system.roles.manage` | Manage custom roles and role permissions |
| `system.providers.manage` | Manage AI providers, model prices, test connections |
| `system.settings.manage` | Global settings, proxy, retention, Telegram bot config |
| `system.audit.view` | View global audit log |
| `system.usage.view` | View global usage/cost dashboards |
| `projects.create` | Create new projects |

Project scope (require membership or a global role granting them):

| Key | Meaning |
|---|---|
| `project.view` | See project and non-sensitive settings |
| `project.settings.manage` | Edit project settings, review policy, budgets; archive project |
| `project.members.manage` | Add/remove members, change member roles |
| `project.schema.manage` | Edit/publish schema versions |
| `project.prompts.manage` | Edit/publish prompt versions |
| `project.ai.manage` | Select provider/model, generation settings |
| `project.playground.use` | Run Playground, manage test cases |
| `project.records.view` | View records and extraction results |
| `project.records.manage` | Create/edit/delete records, manual input, release locks |
| `project.imports.run` | Upload/import files, manage import runs |
| `project.connectors.manage` | Input/output connector CRUD + test + sync |
| `project.processing.run` | Start processing runs |
| `project.processing.control` | Pause/resume/cancel/retry runs |
| `project.assignments.manage` | Assign/reassign/unassign records, manage batches |
| `project.reviews.own` | Review records assigned to self |
| `project.reviews.all` | View all reviews/records regardless of assignment |
| `project.reviews.bulk_approve` | Use bulk approve (when project allows) |
| `project.exports.run` | Run Excel exports, download files |
| `project.deliveries.run` | Run API deliveries, retry failed |
| `project.audit.view` | View project audit log |
| `project.usage.view` | View project usage/costs |

## Seeded roles

| Permission ↓ / Role → | System Administrator (global) | Project Administrator (project) | Operations Manager (project) | Reviewer (project) | Auditor (global) |
|---|---|---|---|---|---|
| `system.users.manage` | ✅ | — | — | — | — |
| `system.roles.manage` | ✅ | — | — | — | — |
| `system.providers.manage` | ✅ | — | — | — | — |
| `system.settings.manage` | ✅ | — | — | — | — |
| `system.audit.view` | ✅ | — | — | — | ✅ |
| `system.usage.view` | ✅ | — | — | — | ✅ |
| `projects.create` | ✅ | — | — | — | — |
| `project.view` | ✅ (all) | ✅ | ✅ | ✅ | ✅ (all, read-only) |
| `project.settings.manage` | ✅ | ✅ | — | — | — |
| `project.members.manage` | ✅ | ✅ | — | — | — |
| `project.schema.manage` | ✅ | ✅ | — | — | — |
| `project.prompts.manage` | ✅ | ✅ | — | — | — |
| `project.ai.manage` | ✅ | ✅ | — | — | — |
| `project.playground.use` | ✅ | ✅ | ✅ | — | — |
| `project.records.view` | ✅ | ✅ | ✅ | own-assigned only¹ | ✅ |
| `project.records.manage` | ✅ | ✅ | ✅ | — | — |
| `project.imports.run` | ✅ | ✅ | ✅ | — | — |
| `project.connectors.manage` | ✅ | ✅ | — | — | — |
| `project.processing.run` | ✅ | ✅ | ✅ | — | — |
| `project.processing.control` | ✅ | ✅ | ✅ | — | — |
| `project.assignments.manage` | ✅ | ✅ | ✅ | — | — |
| `project.reviews.own` | ✅ | ✅ | ✅ | ✅ | — |
| `project.reviews.all` | ✅ | ✅ | ✅ | — | ✅ (read-only²) |
| `project.reviews.bulk_approve` | ✅ | ✅ | ✅ | ✅³ | — |
| `project.exports.run` | ✅ | ✅ | ✅ | — | — |
| `project.deliveries.run` | ✅ | ✅ | ✅ | — | — |
| `project.audit.view` | ✅ | ✅ | ✅ | — | ✅ |
| `project.usage.view` | ✅ | ✅ | ✅ | — | ✅ |

¹ Reviewer record visibility is enforced in queries: `WHERE assigned_reviewer_id = @currentUser` — not just filtered in UI.
² Auditor holds `project.reviews.all` for reading; all mutation endpoints additionally require non-Auditor permissions, so Auditor can never write.
³ Reviewer bulk approve additionally requires the project flag `allowBulkApprove = true` (server-checked, see R20).

## Hard rules

1. Reviewers can never see or modify: schema, prompts, AI provider/model config, connectors, assignment rules, batch settings, validation rules, other reviewers' records.
2. Project isolation: no endpoint returns data across projects the caller isn't a member of (global roles excepted). Enforced by a mandatory `ProjectAccessFilter` on every `/projects/{projectId}/…` route.
3. The last active System Administrator cannot be deactivated or demoted (server-enforced).
4. Hangfire dashboard: `system.settings.manage` only.
5. Every permission denial returns `403` with problem+json code `permission_denied` and is auditable (denials logged at Warning, not stored as audit rows).
