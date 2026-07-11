# 23 — Deployment Plan

## Topology (decision D25)

```
Internet ──▶ caddy (80/443, auto-TLS) ──▶ app (ASP.NET Core :8080)
                                            ├─ SignalR /hubs/*
                                            ├─ Hangfire server (in-process)
                                            └─ SPA static files
             postgres:16  ◀── app
Volumes: pgdata (postgres), appdata (/data: uploads/, exports/, keys/, logs/)
```

## Files (in `docker/`)

- `Dockerfile` — multi-stage: `node:22` builds ClientApp → `dotnet/sdk:10` publishes (SPA output into `wwwroot`) → `dotnet/aspnet:10` runtime, non-root user `structura`, `HEALTHCHECK` on `/health`.
- `docker-compose.yml` (dev): app (mounted appsettings.Development), postgres with exposed 5432, optional WireMock service for provider mocking.
- `compose.prod.yml`: caddy + app + postgres, restart policies `unless-stopped`, healthcheck-gated dependencies, log rotation (json-file, max-size 50m).
- `Caddyfile`: `{$PUBLIC_HOST} { reverse_proxy app:8080 }` — automatic HTTPS; note for running behind an existing proxy (disable Caddy, set forwarded headers).

## Environment variables (`.env.example` — complete, documented)

```
PUBLIC_HOST=structura.example.com          # public origin; used for Telegram webhook + Mini App URL
POSTGRES_DB=structura  POSTGRES_USER=structura  POSTGRES_PASSWORD=<strong>
ConnectionStrings__Default=Host=postgres;Database=structura;Username=structura;Password=<strong>
JWT_SIGNING_KEY=<64+ random bytes base64>
BOOTSTRAP_ADMIN_EMAIL=admin@example.com
BOOTSTRAP_ADMIN_PASSWORD=<initial, forced change on first login>
GLOBAL_PROXY_URL=                          # optional: http://user:pass@host:port or socks5://…
ALLOW_INSECURE_HTTP=false                  # connectors: allow http:// (dev only)
ALLOW_PRIVATE_AI_ENDPOINTS=false           # allow private-IP AI base URLs (Ollama etc.)
RAW_RESPONSE_RETENTION_DAYS=90
EXPORT_FILE_RETENTION_DAYS=30
SEED_DEMO=false
ASPNETCORE_ENVIRONMENT=Production
```
Telegram bot token, provider API keys, connector credentials are **not** env vars — they are entered in the UI and encrypted (doc 02 conventions). Data Protection keys persist in `/data/keys` — losing them makes stored secrets unrecoverable (re-enter via UI); backup includes them.

## First run (production)

1. `cp .env.example .env` → fill values.
2. `docker compose -f docker/compose.prod.yml up -d` — app waits for postgres health, runs migrations under advisory lock, seeds roles + bootstrap admin.
3. Open `https://$PUBLIC_HOST` → sign in with bootstrap credentials → forced password change.
4. Settings → Telegram: paste bot token (from BotFather), mode = webhook, **Set Webhook** (uses `PUBLIC_HOST`); configure Mini App URL `https://$PUBLIC_HOST/tg` in BotFather (`/newapp`, short name `review`).
5. AI Providers → add OpenRouter/NVIDIA key → Test Connection.
6. Optional: set `GLOBAL_PROXY_URL` (or per-provider proxy) where direct egress is unavailable.

## Development workflow

`docker compose up postgres` → `dotnet watch --project src/Structura.Web` + `npm run dev` in ClientApp (Vite proxy to :8080). Telegram in dev: polling mode (no public URL needed). `SEED_DEMO=true` recommended. E2E: `docker compose --profile e2e up` (adds WireMock; `DEMO_PROVIDER_BASE_URL=http://wiremock:8080`).

## Database migrations

Applied automatically at startup (advisory lock). Manual path documented: `dotnet ef database update -p src/Structura.Persistence`. Rollback: migrations are additive; restore-from-backup is the rollback strategy (documented).

## Backup & restore

- `scripts/backup.sh`: `pg_dump -Fc` → `backups/structura-<ts>.dump` + `tar` of `/data` (uploads/exports/keys). Cron example in docs; retention guidance.
- `scripts/restore.sh <dump> <data.tar>`: stop app → restore DB (`pg_restore --clean`) → restore `/data` → start app. Tested as part of T21.2 acceptance.
- Explicit note: backups contain PII + encrypted secrets + the DataProtection keys — store encrypted (doc 16 T18).

## Health & monitoring

- `/health` liveness (process up); `/health/ready`: DB reachable, migrations current, Hangfire storage OK, `/data` writable, Telegram heartbeat (when configured). Caddy healthchecks app; compose restarts on failure.
- Logs: Serilog JSON to stdout (docker logs) + rolling files `/data/logs` (14 d). Correlation ID in every line. Metrics endpoint `/metrics` (Prometheus format, `system.settings.manage`-token guarded) — job counters, provider latency, queue depths.
- Recommended (documented, optional): Uptime check on `/health/ready`, disk alert on volumes, error-rate alert from logs.

## Upgrade procedure

`docker compose pull && docker compose up -d` → new image migrates on start (advisory lock prevents races). Release notes must flag breaking env changes. Rollback = previous image tag + DB restore if a migration landed.

## Scale-out path (documented, not built — R4)

Move DataProtection key ring + SignalR backplane to Redis, run N `app` replicas behind Caddy, pin Hangfire recurring jobs unchanged (Postgres storage already multi-node-safe). No code changes required beyond configuration providers already registered behind flags.
