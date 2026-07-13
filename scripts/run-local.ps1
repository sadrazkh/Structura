# Structura — one-command local run WITHOUT Docker (Windows).
# Runs the whole MVP as a single process: the ASP.NET Core host serves both the API and the
# built Vue SPA on http://localhost:8080. Uses a durable local PostgreSQL cluster under the
# user profile (survives reboots; not the machine temp dir).
#
#   pwsh scripts/run-local.ps1            # build SPA if needed, start DB, run app
#   pwsh scripts/run-local.ps1 -Seed      # also seed demo data on first run
#   pwsh scripts/run-local.ps1 -Rebuild   # force a fresh SPA build

param(
  [switch]$Seed,
  [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
$pgData = Join-Path $env:USERPROFILE '.structura\pgdata'
$port = 5433

function Find-PgBin {
  if (Test-Path $script:pgBin) { return $script:pgBin }
  $found = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
  if ($found) { return (Join-Path $found.FullName 'bin') }
  throw 'PostgreSQL not found. Install PostgreSQL 16+ or run with Docker (docker/docker-compose.yml).'
}
$pgBin = Find-PgBin

# 1. Ensure the durable cluster exists.
if (-not (Test-Path (Join-Path $pgData 'PG_VERSION'))) {
  Write-Host '==> Creating local PostgreSQL cluster...' -ForegroundColor Cyan
  New-Item -ItemType Directory -Force -Path (Split-Path $pgData) | Out-Null
  & "$pgBin\initdb.exe" -D $pgData -U structura -A trust -E UTF8 | Out-Null
}

# 2. Start it if not already accepting connections.
& "$pgBin\pg_isready.exe" -h localhost -p $port *> $null
if ($LASTEXITCODE -ne 0) {
  Write-Host '==> Starting PostgreSQL...' -ForegroundColor Cyan
  & "$pgBin\pg_ctl.exe" -D $pgData -o "-p $port -c shared_buffers=48MB -c max_connections=60" `
    -l (Join-Path $pgData 'pg.log') start | Out-Null
  Start-Sleep -Seconds 4
}

# 3. Ensure the app database exists.
& "$pgBin\psql.exe" -h localhost -p $port -U structura -d postgres -tc `
  "SELECT 1 FROM pg_database WHERE datname='structura'" | Select-String '1' *> $null
if ($LASTEXITCODE -ne 0) {
  & "$pgBin\psql.exe" -h localhost -p $port -U structura -d postgres -c 'CREATE DATABASE structura' | Out-Null
}

# 4. Build the SPA into wwwroot if missing or -Rebuild.
$wwwroot = Join-Path $repo 'src\Structura.Web\wwwroot\index.html'
if ($Rebuild -or -not (Test-Path $wwwroot)) {
  Write-Host '==> Building the SPA...' -ForegroundColor Cyan
  Push-Location (Join-Path $repo 'src\Structura.Web\ClientApp')
  if (-not (Test-Path 'node_modules')) { npm install }
  npm run build
  Pop-Location
}

# 5. Run the single-host app (API + SPA on one port).
$env:ConnectionStrings__Default = "Host=localhost;Port=$port;Database=structura;Username=structura"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:8080'
if ($Seed) { $env:SEED_DEMO = 'true' }

Write-Host ''
Write-Host '==> Structura is starting on http://localhost:8080' -ForegroundColor Green
Write-Host '    Sign in: admin@local.dev / Admin!Passw0rd  (change forced on first login)' -ForegroundColor Green
if ($Seed) { Write-Host '    Demo accounts: pm@demo.local, reviewer1..5@demo.local / Demo!Passw0rd' -ForegroundColor Green }
Write-Host ''
dotnet run --project (Join-Path $repo 'src\Structura.Web') --no-launch-profile -c Release
