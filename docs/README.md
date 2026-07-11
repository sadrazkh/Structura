# Structura — V1 Specification (Lean)

**Structura V1** is a practical operational tool, not an enterprise platform:

> **Admin defines the form, AI processes records, reviewers verify results, and the system exports approved data.**

Two surfaces: an **Admin Panel** and a **Reviewer PWA** (also loaded as a Telegram Mini App). A Telegram bot only sends notifications and opens the Mini App.

This spec replaces the earlier enterprise-scale plan (archived in `../docs-archive-enterprise/`, reference only — **do not implement from it**). Everything needed for V1 is in the files below; anything not written here is out of scope.

| # | File | Content |
|---|------|---------|
| 01 | [01-product-spec.md](01-product-spec.md) | Simplified product spec: scope, roles, field types, statuses, fixed decisions |
| 02 | [02-pages-and-flows.md](02-pages-and-flows.md) | Final page list + core user flows |
| 03 | [03-domain-and-database.md](03-domain-and-database.md) | Domain model + all database tables |
| 04 | [04-api-endpoints.md](04-api-endpoints.md) | Main API endpoints + conventions |
| 05 | [05-processing-and-ai.md](05-processing-and-ai.md) | Background processing + AI integration flow |
| 06 | [06-telegram.md](06-telegram.md) | Telegram bot + Mini App integration |
| 07 | [07-security.md](07-security.md) | Security essentials |
| 08 | [08-milestones.md](08-milestones.md) | 6 implementation milestones, each shippable and testable |
| 09 | [09-implementation-handoff.md](09-implementation-handoff.md) | **Implementation Handoff for Coding AI** (binding) |

## Conventions

- UI language: **English**. Record content is Persian/English → render with `dir="auto"`.
- Stack: ASP.NET Core (.NET 10 LTS) · PostgreSQL 16 · EF Core · Vue 3 + TypeScript (single SPA in the same solution) · SignalR · Docker Compose. **Single web project** with feature folders (modular monolith by folders, not by microservices or many projects).
- IDs: UUID v7. Timestamps: UTC `timestamptz`. DB: snake_case. API JSON: camelCase. Errors: RFC 7807 problem+json with stable `code`.
- No mock data anywhere; every listed feature is real and wired end-to-end.
