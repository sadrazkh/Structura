# 18 — MVP and Roadmap

## MVP (must ship complete, real, non-mock)

**Admin Panel:** authentication + forced password change; user management; role management (seeded + custom); project CRUD + 5-step wizard + members; dynamic schema builder + versioning (draft/publish/diff/restore); AI provider management (OpenRouter + NVIDIA + custom OpenAI-compatible via one adapter) + model prices + test connection + optional proxy; prompt configuration + versioning; Playground + test cases (run/run-all/compare); Excel/CSV import (streaming, mapping, preview, dup handling, row errors, cancel) + manual input (single/bulk paste); REST API input connector (test/preview/manual run/cron schedule/checkpointing); bulk AI processing (all scope modes, durable runs, pause/resume/cancel/retry-failed, budget + error-rate stops, live progress, cost estimate + confirmation); record management (filters incl. dynamic-field containment, detail with AI-vs-human diff, lock release, guarded delete); assignment system (batches, even/by-count/round-robin strategies, reassign/unassign/priority/due, per-row conflict reporting); review operations dashboard; Excel export (column picker, template, child sheets, sanitization, download, retention); REST API output connector (template/mapping, dry run, batch/single, idempotent deliveries, retry, dead-letter); processing history; token+cost tracking with budgets; audit log; global settings (proxy, retention, Telegram); Docker deployment.

**Reviewer App:** login; My Tasks; Review Queue; Focus Review (locking, drafts+autosave, validation, confidence+evidence, AI-original revert, approve/reject/return/skip, prev/next, keyboard); Table Review (inline scalar edit, drawer for complex, bulk approve/reject/return with safeguards); Completed; Progress; notifications; settings + Telegram linking; full PWA (installable, offline shell, offline drafts + sync + conflict UI, update prompt, secure logout).

**Telegram:** bot (webhook + polling modes), account linking, assignment/due/backlog/run-completion notifications, `/tasks /next /progress /open /help`, Mini App (= Reviewer SPA at `/tg`) with initData auth.

## Phase 1.1 (hardening + operator quality-of-life)

- Review policies: warnings-based mode, sensitive-field always-review UI, random sampling of auto-approved records
- Saved record filters; export scheduled runs; import mapping templates
- OpenRouter price auto-sync job; provider health dashboard panel
- Field-level quality report (edit rate per field over time)
- Reviewer skip-with-reason analytics; assignment auto-balancing suggestion
- httpOnly-cookie auth mode option (T14 hardening); ClamAV integration behind `IMalwareScanner`

## Phase 2

- QA Sampling, Double Review, Supervisor Approval, Adjudication (states already reserved)
- Model routing & provider fallback policies (`IModelRouter`); cheap-model/strong-model escalation
- Webhook input connector; Google Sheets + Database + Webhook output connectors
- Anthropic / Gemini / Azure OpenAI / Ollama adapters
- Dynamic-field projection/expression indexes + saved computed columns
- Advanced analytics dashboards; scheduled reports
- Redis-backed scale-out profile (SignalR backplane, DataProtection, N app replicas)

## Future

- Multi-tenancy / workspaces (`workspace_id` introduction), org-level billing
- SFTP / Message Queue connectors, custom plugin SDK (connector + provider plugins)
- OAuth-based connectors; SSO (OIDC) login
- Custom review workflow builder; automatic assignment rules engine
- Record-level chat/comments between admin and reviewer

Guardrails: nothing in MVP may block these (verified by: reserved enum states, connector/provider registries, `IModelRouter` seam, workspace-ready FK layout, policy-shaped review config).
