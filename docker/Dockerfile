# ---------- Stage 1: build the Vue SPA ----------
FROM node:22-alpine AS client
WORKDIR /app/ClientApp
COPY src/Structura.Web/ClientApp/package*.json ./
RUN npm ci
COPY src/Structura.Web/ClientApp/ ./
# vite.config.ts writes the bundle to ../wwwroot → /app/wwwroot
RUN npm run build

# ---------- Stage 2: build and publish the .NET app ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Structura.Web/Structura.Web.csproj src/Structura.Web/
RUN dotnet restore src/Structura.Web/Structura.Web.csproj
COPY src/ src/
COPY --from=client /app/wwwroot src/Structura.Web/wwwroot
RUN dotnet publish src/Structura.Web/Structura.Web.csproj -c Release -o /app/publish

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --create-home --shell /usr/sbin/nologin structura \
 && mkdir -p /data && chown structura:structura /data
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
USER structura
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
  CMD curl -fsS http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "Structura.Web.dll"]
