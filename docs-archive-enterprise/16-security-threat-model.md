# 16 — Security & Threat Model

## Baseline controls (implementation requirements)

- **AuthN:** ASP.NET Core Identity password hashing (PBKDF2 default work factor ≥ Identity v3), JWT RS256 or HS512 with key from env (≥64 bytes), access 15 min / refresh 14 d rotated + reuse-detection revocation, login rate limit 5/min/IP + account lockout 10 fails/15 min, `must_change_password` gate, session invalidation via `security_stamp` on password change/deactivation.
- **AuthZ:** permission-per-endpoint + `ProjectAccessFilter` + query-level reviewer scoping (doc 03). Server-side always; UI hiding is cosmetic.
- **Secrets at rest:** Data Protection key ring on `/data/keys` (filesystem, chmod 700 container), `ISecretProtector` for provider keys, connector creds, bot token, proxy creds. Masked API output, replace-only writes, redaction filter in Serilog (denylist: `password, token, apiKey, authorization, secret, initData` + `protected:` values).
- **Transport:** HTTPS via Caddy (auto-TLS); HSTS; secure headers middleware (CSP `default-src 'self'` + hashed inline for SPA bootstrap, `frame-ancestors 'none'` except `/tg` route which allows Telegram origins per Mini App requirements, `nosniff`, `referrer-policy: same-origin`).
- **Uploads:** extension allowlist (.xlsx/.csv), MIME sniff (magic bytes: zip header for xlsx, text heuristic for csv), size cap 100 MB, stored outside webroot with random names, never executed/served raw (row-error CSV generated fresh), ClamAV integration point stubbed behind `IMalwareScanner` (no-op MVP, interface + config ready).
- **XSS:** Vue escaping by default; `v-html` forbidden by lint rule; all record/AI content rendered as text (`dir=auto`); CSP backstop. **CSRF:** bearer-token API, no cookie auth ⇒ not applicable; SignalR uses JWT query token over WSS.
- **SQLi:** EF Core parameterization only; dynamic field filters compiled to parameterized `@>` containment JSON — never string-concatenated SQL; raw SQL requires review comment `-- reviewed-raw-sql`.
- **Rate limiting:** ASP.NET `RateLimiter` — global 300 req/min/user, auth endpoints stricter, playground 10/min/user, bulk endpoints 30/min.
- **SSRF/egress:** doc 14 (single choke point). **Excel injection:** doc 12 §6. **Prompt injection:** doc 13. **Telegram:** doc 15.
- **Data retention/deletion:** R12/R22/R24 + settings-driven cleanup; export files access-controlled (auth + project permission on download route, no static serving).
- **Provider privacy warning:** provider create/edit screen shows fixed notice: "Record text will be sent to this provider's API. Verify the provider's data-use policy before processing sensitive data." — acknowledged checkbox stored.
- **Audit:** every state change (doc 02 conventions); admin/audit read access per doc 03.

## Threat model

Scale: Impact/Probability = Low/Med/High. Residual assumes mitigations implemented.

| # | Risk | Impact | Prob | Mitigation | Residual |
|---|---|---|---|---|---|
| T1 | Prompt injection in source text alters extraction or exfiltrates prompt | Med (bad data → caught by review) | High | Nonce-delimited input, guard preamble, structured output, schema validation, no tools, human review layer | Low — wrong values can still slip; review + edit-rate monitoring detect |
| T2 | SSRF via input/output connector URL (cloud metadata, internal services) | High | Med | SafeHttp vetting, IP pinning (anti-rebinding), redirect re-validation, egress lists, private-range block | Low |
| T3 | Excel formula injection in exported files | Med | Med | Cell sanitization (`'` prefix on `=+-@\t\r`), test coverage | Low |
| T4 | API key / bot token leakage via UI, logs, snapshots, errors | High | Med | Encryption at rest, masking, log redaction, snapshots exclude secrets, attempt-excerpt scrubbing | Low |
| T5 | Reviewer accesses records not assigned (IDOR) | Med | Med | Query-scoped access, ProjectAccessFilter, permission tests per endpoint | Low |
| T6 | Telegram account takeover → data access via Mini App | High | Low | Hashed single-use short-lived codes, rate limits, link notifications, admin revoke, initData HMAC + age check | Low |
| T7 | Forged Mini App initData | High | Low | HMAC-SHA256 validation with bot token, `auth_date` window, link requirement | Low |
| T8 | Malicious uploaded file (zip bomb xlsx, malformed CSV) | Med | Med | Size caps, streaming parse with row/cell limits, entry-count/uncompressed-size guard on xlsx, MIME check, scanner interface | Low |
| T9 | Concurrent edit data loss / silent overwrite | Med | High | Locks + optimistic versioning + conflict UI (D16/D17), approve transaction guards | Low |
| T10 | Duplicate/ghost processing after crash doubles cost | Med | Med | Idempotent tasks, unique extraction constraint, heartbeat recovery, budgets as backstop | Low — bounded duplicate cost documented |
| T11 | Budget overrun via runaway run or price misconfig | Med | Med | Pre-run estimate + confirmation, per-run/day/month caps checked in-loop, StoppedByBudget state | Low |
| T12 | Duplicate delivery to external API (double writes downstream) | High | Med | DB partial-unique Delivered constraint, idempotency keys, redeliver-explicit flow | Low |
| T13 | Privilege escalation via role editing (self-grant) | High | Low | Seeded roles immutable, `system.roles.manage` restricted, last-admin guard, audit | Low |
| T14 | JWT theft via XSS (refresh in localStorage) | High | Low | CSP, no v-html, escaping, short access TTL, refresh rotation + reuse detection, logout-all on password change | Med-Low — accepted tradeoff, documented; httpOnly-cookie mode listed as Phase 2 hardening option |
| T15 | DoS via expensive endpoints (playground, imports, estimates) | Med | Med | Rate limits, size caps, per-user concurrency guard on playground, background processing isolation | Low |
| T16 | Sensitive record content sent to third-party AI provider without awareness | High | Med | Provider privacy acknowledgment, per-project provider binding visible, audit of config changes | Med — inherent to product function; organizational control |
| T17 | Hangfire dashboard exposure | Med | Low | Auth filter requiring `system.settings.manage`, path not linked publicly | Low |
| T18 | Backup/exports contain PII unencrypted at rest | Med | Med | Documented ops guidance (encrypted volumes/backup targets), retention limits, access-controlled downloads | Med — operational responsibility |
| T19 | Webhook endpoint abuse (fake Telegram updates) | Med | Low | Secret path + secret-token header, 404 on mismatch, idempotent update ids, rate metric alarm | Low |
| T20 | Malicious admin exfiltrates data (insider) | High | Low | Full audit trail, Auditor role separation, export events audited with row counts | Med — detective, not preventive |

## Security test requirements (minimum)

1. SSRF suite: metadata IPs, localhost, private ranges, DNS-rebinding simulation (WireMock + hosts control), redirect-to-private, oversized response.
2. AuthZ matrix tests: every endpoint × every role (autogenerated from permission table) asserting 403s.
3. Reviewer isolation: cross-reviewer record access attempts.
4. Excel injection corpus export test; upload fuzz corpus (zip bomb, wrong magic bytes, huge cells).
5. Telegram: invalid HMAC, expired auth_date, replayed link codes, revoked link access.
6. Rate limit + lockout behavior tests.
7. Secret-leak scan test: API responses and log output snapshots grepped for seeded secret values in integration tests.
