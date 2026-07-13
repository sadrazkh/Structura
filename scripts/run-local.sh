#!/usr/bin/env bash
# Structura — one-command local run WITHOUT Docker.
# Runs the whole MVP as a single process: the ASP.NET Core host serves both the API and the
# built Vue SPA on http://localhost:8080, backed by a durable local PostgreSQL cluster under
# the user home (not the machine temp dir).
#
#   ./scripts/run-local.sh            # build SPA if needed, start DB, run app
#   SEED=1 ./scripts/run-local.sh     # also seed demo data on first run
#   REBUILD=1 ./scripts/run-local.sh  # force a fresh SPA build
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
port=5433
pgdata="${STRUCTURA_PGDATA:-$HOME/.structura/pgdata}"

# Locate a PostgreSQL bin dir (installed server tools).
pgbin=""
for c in "/c/Program Files/PostgreSQL"/*/bin "/usr/lib/postgresql"/*/bin /usr/bin; do
  [ -x "$c/pg_ctl" ] && pgbin="$c" && break
done
[ -n "$pgbin" ] || { echo "PostgreSQL not found — install it or use Docker (docker/docker-compose.yml)."; exit 1; }

# 1. Ensure the durable cluster exists.
if [ ! -f "$pgdata/PG_VERSION" ]; then
  echo "==> Creating local PostgreSQL cluster..."
  mkdir -p "$(dirname "$pgdata")"
  "$pgbin/initdb" -D "$pgdata" -U structura -A trust -E UTF8 >/dev/null
fi

# 2. Start it if not already up.
if ! "$pgbin/pg_isready" -h localhost -p "$port" >/dev/null 2>&1; then
  echo "==> Starting PostgreSQL..."
  "$pgbin/pg_ctl" -D "$pgdata" -o "-p $port -c shared_buffers=48MB -c max_connections=60" \
    -l "$pgdata/pg.log" start >/dev/null
  sleep 4
fi

# 3. Ensure the app database exists.
if ! "$pgbin/psql" -h localhost -p "$port" -U structura -d postgres -Atc \
     "SELECT 1 FROM pg_database WHERE datname='structura'" | grep -q 1; then
  "$pgbin/psql" -h localhost -p "$port" -U structura -d postgres -c "CREATE DATABASE structura" >/dev/null
fi

# 4. Build the SPA into wwwroot if missing or REBUILD=1.
if [ "${REBUILD:-0}" = "1" ] || [ ! -f "$repo/src/Structura.Web/wwwroot/index.html" ]; then
  echo "==> Building the SPA..."
  ( cd "$repo/src/Structura.Web/ClientApp" && { [ -d node_modules ] || npm install; } && npm run build )
fi

# 5. Run the single-host app (API + SPA on one port).
export ConnectionStrings__Default="Host=localhost;Port=$port;Database=structura;Username=structura"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:8080
[ "${SEED:-0}" = "1" ] && export SEED_DEMO=true

echo ""
echo "==> Structura starting on http://localhost:8080"
echo "    Sign in: admin@local.dev / Admin!Passw0rd (change forced on first login)"
[ "${SEED:-0}" = "1" ] && echo "    Demo: pm@demo.local, reviewer1..5@demo.local / Demo!Passw0rd"
echo ""
exec dotnet run --project "$repo/src/Structura.Web" --no-launch-profile -c Release
