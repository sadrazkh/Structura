# 21 — Testing Strategy

## Principles

- Tests are written **per task** (doc 19 global DoD), not deferred to a test phase. E22 is a gate, not the test effort.
- Critical paths are tested beyond happy path — the edge-case lists in doc 19 and the matrices below are mandatory minimums.
- No mocking of the database in integration tests: **Testcontainers PostgreSQL** everywhere. No calls to real AI providers in CI: **WireMock.Net** stands in for OpenAI-compatible, connector, and Telegram APIs.

## Layers

| Layer | Tooling | Scope & notable suites |
|---|---|---|
| Unit (C#) | xUnit + FluentAssertions | FieldSpec validation; SchemaOutputValidator (shared vectors); JSON Schema generator per type; parser/repair corpus; PromptBuilder golden files (fa/en); diff/normalization (D18); template engine incl. injection attempts; Excel sanitization; state machines (full transition matrices — every legal + a set of illegal transitions each); cost math; cursor codec; MarkdownV2 escaping |
| Unit (TS) | Vitest | TS validation engine vs **the same shared vector file** as C# (parity guarantee); dynamic form utils (defaults/normalize); draft sync engine reducer; date/dir handling |
| Integration (API) | xUnit + Testcontainers + WebApplicationFactory | every endpoint happy path + doc 19 edge cases; **generated authz matrix** (endpoint × role); project isolation; reviewer scoping; version-conflict paths; idempotency-key replay |
| Integration (DB) | same | constraint behavior: Delivered-partial-unique race, Active-assignment-unique race, extraction (record,run,attempt) unique, advisory-lock migration |
| Integration (jobs) | same + WireMock | import 50k streaming/memory/resume-after-kill; run kill-and-recover; pause/resume/cancel; budget & error-rate stops; connector pagination/checkpoint resume; delivery retry/dead-letter/idempotent replay; cleanup retention |
| Provider contract | WireMock scenario packs | OpenAI-dialect variants: usage block absent, structured-output refusal, 429 Retry-After, truncated JSON, non-UTF8; OpenRouter-specific headers/params asserted on requests |
| Connector | WireMock | SSRF reference suite (doc 16): metadata/localhost/private/rebind/redirect-to-private/oversize; auth modes; JSONPath failures |
| Security | integration + Playwright | doc 16 §tests 1–7; secret-leak grep of responses/logs; upload fuzz corpus; rate-limit/lockout |
| E2E | Playwright vs compose stack | doc 22 demo flow end-to-end (provider = WireMock via env); PWA: install manifest audit, offline draft → sync, conflict path, update flow, logout guard; Telegram Mini App auth with fixture initData; RTL rendering of Persian record content; keyboard-only Focus Review pass |
| Performance sanity | scripted (T22.3) | import 50k < 5 min; 5k-record run vs mock at concurrency 16 completes with flat memory; records grid p95 < 500 ms at 200k rows; export 50k rows < 3 min |
| Accessibility | axe-core in Playwright | all admin + reviewer pages: no serious violations |

## Cross-cutting mandatory suites

1. **Concurrency pack:** two-session lock contention; concurrent draft saves (one 409); concurrent approve+reassign; concurrent publish of schema draft; assignment double-create.
2. **Idempotency pack:** every ⏯ endpoint (doc 11) called twice with same key → single side effect; every job class executed twice on same input → single domain effect.
3. **Recovery pack:** host kill during — import, run, export, delivery — then restart: state converges, no duplicates, no orphan `Running`.
4. **Permission pack:** the generated matrix + explicit negative tests for the doc 03 "hard rules".
5. **Data-integrity pack (doc 32 scenarios):** each of the 19 reliability scenarios in the brief maps to at least one automated test; a traceability table in the test project README links scenario → test name.

## CI pipeline

`lint (dotnet format, eslint) → build → unit (C#+TS) → integration (Testcontainers) → SPA build → E2E (compose up, WireMock provider) → security pack → artifact (docker image)`. Any red = no merge. Coverage tracked, thresholds: domain/services ≥ 80% lines, no threshold theater elsewhere — the mandatory suites above are the real gate.
