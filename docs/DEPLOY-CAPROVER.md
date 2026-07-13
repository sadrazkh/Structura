# Deploying Structura to CapRover

Structura ships as a single container (ASP.NET Core host serving the API **and** the built Vue
SPA on one port). CapRover builds it from the repo's `docker/Dockerfile` via the root
`captain-definition` file, and provides the domain, HTTPS, and reverse proxy.

## 1. Create the PostgreSQL app

In the CapRover dashboard → **Apps → One-Click Apps/Databases → PostgreSQL** (v16). Note the
values you set — CapRover exposes the DB inside the cluster at `srv-captain--<appname>`.

Example: app name `structura-db`, user `structura`, password `<db-password>`, db `structura`
→ internal host `srv-captain--structura-db`.

## 2. Create the app

**Apps → Create New App** → name `structura` (leave "Has Persistent Data" **checked** — the app
stores Data Protection keys, uploads and exports under `/data`).

### Environment variables (App Configs → Environmental Variables)

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ConnectionStrings__Default` | `Host=srv-captain--structura-db;Port=5432;Database=structura;Username=structura;Password=<db-password>` |
| `JWT_SIGNING_KEY` | a long random secret — `openssl rand -base64 48` |
| `BOOTSTRAP_ADMIN_EMAIL` | your admin email |
| `BOOTSTRAP_ADMIN_PASSWORD` | initial admin password (change is forced at first login) |
| `OUTBOUND_PROXY_URL` | *(optional)* egress proxy for AI providers / Telegram |
| `SEED_DEMO` | *(optional)* `true` to preload demo data on first boot |

### Container HTTP port (App Configs)

Set **Container HTTP Port = `8080`** (the app listens on 8080). Save & Update.

### Persistent directory (App Configs → Persistent Directories)

Add: **Path in App = `/data`**, label e.g. `structura-data`. This keeps the Data Protection key
ring (which encrypts provider API keys and the Telegram bot token) across redeploys — without it,
stored secrets become unrecoverable after every deploy.

> With a persistent directory, keep the app at **1 instance** (the local `/data` volume is not shared).

## 3. Deploy

From the repo root, using the CapRover CLI:

```bash
npm i -g caprover
caprover deploy            # pick your CapRover machine + the `structura` app
```

CapRover tars the repo (respecting `.dockerignore`), reads `captain-definition`, builds
`docker/Dockerfile`, and starts the container. Database migrations run automatically at startup
under a Postgres advisory lock, and the bootstrap admin is created on first run.

Alternatively: **Deployment → Method: Tarball / Git** and point CapRover at this repo.

## 4. Enable HTTPS

**App → HTTP Settings → Enable HTTPS** (Let's Encrypt), then **Force HTTPS**. The app already
honors `X-Forwarded-Proto`/`X-Forwarded-For` from CapRover's proxy.

## 5. First run

Open `https://structura.<your-root-domain>`, sign in with the bootstrap admin, set a new password.
Then configure a project (AI provider → schema → import → process → assign → review → export).

## 6. Telegram (optional)

**Settings → Telegram** (admin): set the public base URL to your CapRover HTTPS URL, paste the
bot token, choose **Webhook**, click **Set Webhook**. In BotFather, set the Mini App URL to
`https://structura.<your-root-domain>/tg`.

## Notes

- **Health check:** the container exposes `GET /api/health`; CapRover marks the app healthy when it passes.
- **Logs:** structured JSON to stdout — view under **App → Deployment / App Logs**.
- **Backups:** back up the PostgreSQL app's data **and** the `/data` persistent directory (it holds
  the encryption keys). Store backups encrypted — they contain PII and secrets.
- **Scaling:** the background workers (import, processing, delivery, Telegram polling) assume a
  single instance. Keep instance count = 1 for the MVP.
