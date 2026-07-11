# Structura — Setup & Operations Guide

This guide takes a fresh machine from clone to a running, usable Structura install.

## 1. Prerequisites

- **Docker** + Docker Compose (production / one-command run), **or**
- **.NET 10 SDK**, **Node 20+**, and a **PostgreSQL 16** instance (local development).

## 2. Run with Docker (recommended)

```bash
git clone <repo> structura && cd structura
cp .env.example .env
# Edit .env: set POSTGRES_PASSWORD, JWT_SIGNING_KEY, BOOTSTRAP_ADMIN_* , PUBLIC_HOST
docker compose -f docker/docker-compose.yml up -d --build
```

- Open `https://localhost` (accept the local self-signed cert) — or `https://PUBLIC_HOST` in production.
- Sign in with `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`; you'll be forced to set a new password.
- Migrations run automatically on startup (under a Postgres advisory lock, safe with multiple instances).

Generate a signing key: `openssl rand -base64 48`.

## 3. Local development

```bash
# 1. Database (Docker) — or point at any local PostgreSQL
docker compose -f docker/docker-compose.dev.yml up -d

# 2. Backend (http://localhost:8080)
dotnet watch --project src/Structura.Web

# 3. Frontend with hot reload (http://localhost:5173, proxies /api + /hubs to :8080)
cd src/Structura.Web/ClientApp && npm install && npm run dev
```

Dev bootstrap admin: `admin@local.dev` / `Admin!Passw0rd` (from `appsettings.Development.json`).

## 4. Configure a project

1. **AI provider** — Project → AI Settings: pick OpenRouter or NVIDIA, paste an API key (stored
   encrypted), choose a model, click **Test Connection**.
2. **Schema** — Project → Schema: define the output fields, Save.
3. **Import** — Project → Import: upload an Excel/CSV (map ID + Text columns), paste manually, or fetch from an API.
4. **Process** — Project → Runs → *Process All Pending* (or select records → Process).
5. **Assign** — Project → Review: pick reviewers, *Assign All Ready Records*.
6. **Review** — reviewers sign in (web or Telegram Mini App), approve/reject/reprocess.
7. **Output** — Project → Output: download Excel, or configure the API connector for auto-delivery.

## 5. Telegram (optional)

1. Create a bot with [@BotFather](https://t.me/BotFather); copy the token.
2. **Settings → Telegram** (admin): set the public base URL, paste the bot token, pick a mode:
   - **Webhook** (production): click **Set Webhook** (needs a public HTTPS URL).
   - **Polling** (dev / restricted networks): no public URL required.
3. Configure the Mini App in BotFather (`/newapp`) with URL `https://PUBLIC_HOST/tg`.
4. Reviewers link their account: **Reviewer → Settings → Telegram → Generate code**, then send
   `/start THECODE` to the bot.

Notifications (new assignment, reprocess-ready, run finished) then arrive in Telegram, and the
**Open review app** button launches the Mini App.

## 6. Demo data

Set `SEED_DEMO=true` (dev, or Production with `SEED_DEMO_FORCE=true`) before first start. Seeds a
project manager (`pm@demo.local`), five reviewers (`reviewer1..5@demo.local`), and a project with
60 already-processed Persian/English records (50 assigned, 6 approved) so every screen is populated
without a live provider. Password for all demo accounts: `Demo!Passw0rd`.

## 7. Backup & restore

```bash
# Backup: database + the /data volume (uploads, exports, Data Protection keys)
docker compose -f docker/docker-compose.yml exec -T postgres \
  pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB" > backup-$(date +%F).sql
docker run --rm -v structura_appdata:/data -v "$PWD:/out" alpine \
  tar czf /out/appdata-$(date +%F).tgz -C /data .

# Restore
docker compose -f docker/docker-compose.yml exec -T postgres \
  psql -U "$POSTGRES_USER" "$POSTGRES_DB" < backup-YYYY-MM-DD.sql
docker run --rm -v structura_appdata:/data -v "$PWD:/out" alpine \
  sh -c "cd /data && tar xzf /out/appdata-YYYY-MM-DD.tgz"
```

> The `/data` volume holds the Data Protection key ring — losing it makes stored secrets
> (provider keys, bot token) unrecoverable and they must be re-entered. Always back it up, and
> store backups encrypted (they contain PII + secrets).

## 8. Health & operations

- `GET /api/health` — liveness + database check (used by the container healthcheck).
- Structured JSON logs go to stdout (`docker compose logs -f app`); each line carries a correlation id.
- Background workers (import, processing, delivery, Telegram) resume automatically after a restart —
  the database is the queue, so no work is lost.

## 9. Tests

```bash
dotnet test                              # Testcontainers PostgreSQL (needs Docker)
# or, without Docker, against a dedicated local database (dropped & recreated per run):
STRUCTURA_TEST_DB="Host=localhost;Port=5432;Database=structura_test;Username=structura" dotnet test
```
