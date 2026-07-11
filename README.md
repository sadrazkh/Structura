# Structura

Admin-configured, AI-processed, human-reviewed text extraction:

> **Admin defines the form, AI processes records, reviewers verify results, and the system exports approved data.**

Full specification lives in [`docs/`](docs/README.md). Implementation progress follows the milestones in [`docs/08-milestones.md`](docs/08-milestones.md) — currently **Milestone 1 (Foundation)** is implemented: authentication, user management, projects & members, running in Docker.

## Stack

ASP.NET Core (.NET 10) · PostgreSQL 16 · EF Core · Vue 3 + TypeScript + Tailwind (single SPA served by the backend) · Docker Compose.

## Quick start (Docker)

```bash
cp .env.example .env          # then edit: passwords, JWT key, bootstrap admin
docker compose -f docker/docker-compose.yml up -d --build
```

Open `https://localhost` (accept the local self-signed certificate), sign in with `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD`, and set a new password when prompted.

## Development

Requirements: .NET 10 SDK, Node 20+, Docker (for the dev database and tests).

```bash
# 1. Database
docker compose -f docker/docker-compose.dev.yml up -d

# 2. Backend (http://localhost:8080)
dotnet watch --project src/Structura.Web

# 3. Frontend with hot reload (http://localhost:5173, proxies /api to 8080)
cd src/Structura.Web/ClientApp && npm install && npm run dev
```

Development sign-in: `admin@local.dev` / `Admin!Passw0rd` (from `appsettings.Development.json`; a password change is forced on first sign-in).

## Tests

Integration tests boot the real app against a disposable PostgreSQL container (Docker required):

```bash
dotnet test
```

No Docker available? Point the suite at any local PostgreSQL instead — the target database is
dropped and recreated on every run, so use a dedicated database:

```bash
STRUCTURA_TEST_DB="Host=localhost;Port=5433;Database=structura_test;Username=structura" dotnet test
```

## Repository layout

```
src/Structura.Web/        ASP.NET Core host — Domain / Persistence / Infrastructure / Features
src/Structura.Web/ClientApp/   Vue 3 SPA (built into wwwroot)
tests/Structura.Tests/    xUnit integration tests (Testcontainers)
docker/                   Dockerfile, compose files, Caddyfile
docs/                     Product & architecture specification (V1)
```
