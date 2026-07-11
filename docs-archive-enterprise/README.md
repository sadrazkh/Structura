# Structura — Product, UX, Architecture & Implementation Specification

**Structura** is an admin-first, AI-powered, human-reviewed platform that converts unstructured text into structured data.

> Core principle: **Admin-configured, AI-processed, human-reviewed, and system-delivered.**

Admins define a dynamic output schema and extraction instructions, import records (Excel/CSV, manual, API), run durable bulk AI processing, distribute records to human reviewers (web PWA + Telegram Mini App), and deliver approved results via Excel export or an outbound API connector.

This documentation set is a **complete, decision-final specification**. It is written to be handed directly to a coding AI (or an engineering team) for full implementation — no prototype, no mock data, production-deployable.

- Version: 1.0
- Date: 2026-07-11
- Product codename: `Structura` (rename is a find/replace; nothing else depends on it)

## Document Map

| # | File | Content |
|---|------|---------|
| 01 | [01-product-overview.md](01-product-overview.md) | Executive summary, clarified requirements, assumptions, non-goals |
| 02 | [02-architecture-decisions.md](02-architecture-decisions.md) | The 25 binding architecture decisions + final technology stack + solution structure |
| 03 | [03-roles-permissions.md](03-roles-permissions.md) | Roles, permission constants, full permission matrix |
| 04 | [04-user-flows.md](04-user-flows.md) | All 33 end-to-end product flows, step by step |
| 05 | [05-information-architecture.md](05-information-architecture.md) | Global/admin/project/reviewer navigation, settings hierarchy |
| 06 | [06-ux-specification.md](06-ux-specification.md) | Per-page UX spec (purpose, layout, actions, states, validation, mobile, permissions) + design system |
| 07 | [07-wireframes.md](07-wireframes.md) | Text wireframes for the 14 key screens |
| 08 | [08-domain-model.md](08-domain-model.md) | Entities, aggregates, value objects, relationships, schema definition format |
| 09 | [09-state-machines.md](09-state-machines.md) | All state machines with valid/invalid transitions and backend enforcement rules |
| 10 | [10-database-design.md](10-database-design.md) | Tables, columns, keys, indexes, JSONB usage, concurrency, retention, versioning |
| 11 | [11-api-design.md](11-api-design.md) | API conventions, error model, full endpoint catalog, detailed contracts for complex endpoints |
| 12 | [12-background-jobs.md](12-background-jobs.md) | Job catalog: input/output, retry, idempotency, recovery, dead-letter, metrics |
| 13 | [13-ai-integration.md](13-ai-integration.md) | Provider abstraction, prompt construction, JSON Schema generation, parse/repair/validate, cost tracking, injection defense |
| 14 | [14-connector-architecture.md](14-connector-architecture.md) | Input/output connector interfaces, lifecycle, safe outbound HTTP |
| 15 | [15-telegram-architecture.md](15-telegram-architecture.md) | Bot lifecycle, webhook/polling, account linking, Mini App auth, notifications |
| 16 | [16-security-threat-model.md](16-security-threat-model.md) | Threat model with risk/impact/probability/mitigation/residual risk |
| 17 | [17-error-handling-matrix.md](17-error-handling-matrix.md) | Error handling matrix for all mandated failure scenarios |
| 18 | [18-mvp-roadmap.md](18-mvp-roadmap.md) | MVP / Phase 1.1 / Phase 2 / Future feature split |
| 19 | [19-implementation-plan.md](19-implementation-plan.md) | Epics → features → tasks with acceptance criteria, edge cases, tests, DoD |
| 20 | [20-build-order.md](20-build-order.md) | Exact dependency-ordered build sequence |
| 21 | [21-testing-strategy.md](21-testing-strategy.md) | Full test strategy per layer and per risk area |
| 22 | [22-demo-seed-scenario.md](22-demo-seed-scenario.md) | Demo/seed data scenario end to end |
| 23 | [23-deployment-plan.md](23-deployment-plan.md) | Docker, Compose, env vars, migrations, first admin, HTTPS, backup/restore, webhook |
| 24 | [24-implementation-handoff.md](24-implementation-handoff.md) | **Implementation Handoff for Coding AI** — binding rules, conventions, completion criteria |

## How to use this spec (for the coding AI)

1. Read [24-implementation-handoff.md](24-implementation-handoff.md) first — it is the binding contract.
2. Read [02-architecture-decisions.md](02-architecture-decisions.md) — every decision there is final; do not re-open it.
3. Build in the exact order given by [20-build-order.md](20-build-order.md), taking task definitions from [19-implementation-plan.md](19-implementation-plan.md).
4. All names (entities, tables, endpoints, permissions, statuses) used across these documents are canonical — use them verbatim.

## Global conventions

- Product UI language: **English only**. Record content is bidirectional (Persian/English) and rendered with `dir="auto"`.
- All timestamps stored in UTC (`timestamptz`), displayed in the user's local timezone.
- All identifiers: UUID v7.
- Costs tracked in USD.
- C# naming: PascalCase; DB naming: snake_case; JSON API: camelCase; permission keys / field keys: dot.case / camelCase respectively.
