# 07 — Security Essentials

The V1 list — every item is required, nothing beyond it is.

1. **Authentication:** ASP.NET Core Identity password hashing; JWT access 15 min + rotating refresh 14 d (hashed, reuse ⇒ revoke family); login rate limit (5/min/IP) + lockout (10 fails / 15 min); `must_change_password` gate; deactivation kills sessions (security stamp claim checked).
2. **Backend authorization:** role + project-membership checks on every endpoint; reviewer data access always query-filtered by `assigned_reviewer_id`; last-active-Administrator cannot be deactivated. UI hiding is never the control.
3. **Encrypted API keys / secrets:** ASP.NET Data Protection (key ring persisted to `/data/keys`); provider keys, output-connector credentials, bot token stored `protected:v1:…`; replace-only writes.
4. **Secret masking:** API responses show `••••` + last 4; secrets never appear in logs, error messages, run snapshots, or SignalR payloads.
5. **Input validation:** FluentValidation on every request; schema-field definition validated (key format/uniqueness, allowedValues for selects); AI output validated server-side; approve re-validates (required + types + allowedValues) — client-side validation is convenience only.
6. **Safe file upload:** extension allowlist (`.xlsx`, `.csv`), magic-byte check (zip header / text heuristic), 50 MB size cap, 100k row cap, per-cell 256 KB cap, stored under `/data/uploads` with random names, never served back raw.
7. **SSRF protection (API input + API output + test buttons):** one `SafeHttpClient`: https only (`ALLOW_INSECURE_HTTP=true` for dev), resolve DNS → block loopback/RFC1918/link-local incl. 169.254.169.254/ULA/CGNAT, pin connection to the vetted IP (defeats DNS rebinding), redirects off, response cap 10 MB, timeout 30 s, header allowlist (no `Host`/hop-by-hop; auth only via typed config).
8. **Excel formula injection protection:** every exported string cell prefixed with `'` when it starts with `= + - @ TAB CR`.
9. **Sensitive log redaction:** Serilog enricher/destructuring policy drops `password, token, apiKey, authorization, initData` and any `protected:` value; provider/connector request logs record URL + status + duration only.
10. **Prompt-injection containment:** untrusted text delimited in `<source_text>` tags + guard instruction + strict JSON schema output + server-side validation; record content always rendered as text (`v-html` forbidden), CSP `default-src 'self'` (with `frame-ancestors` exception only on `/tg` for Telegram).
11. **Telegram:** hashed single-use 10-min link codes; webhook secret path + secret-token header; initData HMAC + age check; admin revoke.
12. **Transport & headers:** HTTPS via reverse proxy (Caddy in compose), HSTS, `X-Content-Type-Options`, same-origin referrer policy; SQL injection covered by EF parameterization (no string-built SQL).
