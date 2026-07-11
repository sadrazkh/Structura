# 14 — Connector Architecture

## Shared foundation: SafeHttpClientFactory

All connector and provider traffic uses one factory (decisions D12/D13). Profiles:

| Profile | Used by | Policy |
|---|---|---|
| `ConnectorEgress` | input/output connectors, Telegram | full SSRF vetting, redirects manual ≤3, response cap 10 MB, header allowlist |
| `AiEgress` | AI providers | same vetting unless `ALLOW_PRIVATE_AI_ENDPOINTS=true`; response cap 50 MB |

Pipeline per request: URL parse → scheme check → DNS resolve → IP vetting (block loopback/RFC1918/link-local/metadata/ULA/CGNAT/multicast; optional installation allowlist/denylist) → pinned `ConnectCallback` to the vetted IP (TLS SNI/Host header keep original hostname) → proxy resolution (connector override → global → none; when a proxy is configured, vetting still applies to the URL host, and connections route via proxy) → send with timeout → stream response with byte cap → redirect? re-enter pipeline (≤3).

## Input connectors

### Interface & lifecycle

```csharp
public interface IInputConnector
{
    InputConnectorType Type { get; }                       // MVP: RestApi. Future: Webhook, Database, Sftp
    Task<ConnectorTestResult> TestAsync(InputConnectorConfig cfg, CancellationToken ct);
    Task<ConnectorPreview> PreviewAsync(InputConnectorConfig cfg, int maxRecords, CancellationToken ct);
    IAsyncEnumerable<SourceRecordPage> FetchAsync(InputConnectorConfig cfg, SyncCheckpoint? checkpoint,
                                                  CancellationToken ct);  // paged, checkpoint-aware
}
public sealed record SourceRecord(string? ExternalId, string Text, IReadOnlyDictionary<string, object?> Metadata);
public sealed record SourceRecordPage(IReadOnlyList<SourceRecord> Records, SyncCheckpoint NextCheckpoint, bool HasMore);
```

Lifecycle: `Configure → Test → Preview (raw + mapped) → Save → Run (manual|scheduled) → pages fetched → per page: map, dedupe, insert, commit checkpoint → run summary`. Registry pattern identical to providers.

### RestApiInputConnector (MVP)

- Config (JSONB): connection (baseUrl, endpoint, method GET/POST, headers, query params, auth: none/apiKey(header or query)/bearer/basic — credentials `protected:`), request body (POST), pagination (`none | page {pageParam, sizeParam, pageSize} | cursor {cursorPath, cursorParam} | nextUrl {nextPath}`), extraction (`dataPath` JSONPath → array; `idPath`, `textPath`, `metadataMappings: [{key, path}]`), incremental (`syncField` JSONPath + `sinceParam` — checkpoint stores max seen value), limits (timeout, maxResponseBytes, retryPolicy), schedule (cron), proxy override.
- JSONPath via `JsonPath.Net`; invalid path at config time = validation error; at runtime = row error.
- **Idempotency/dedup:** `(project_id, external_id)` unique — existing IDs skipped and counted; missing `idPath` ⇒ deterministic hash of text+metadata as external id (`API-<sha256-12>`), preventing duplicates across syncs.
- **Checkpointing:** `sync_checkpoint` JSONB committed after each page transaction; crash resumes from last committed page. Manual "Reset checkpoint" action (admin, confirm) re-syncs from scratch relying on dedup.
- Sync history: each execution creates an `ImportRun (source_type=Connector)` with counters + error.

### Webhook input (future-ready)

Reserved: `InputConnectorType.Webhook` + route `POST /api/v1/inbound/{connectorId}/{secret}` — not implemented in MVP; the connector table/config format already accommodates it.

## Output connectors

### Interface & lifecycle

```csharp
public interface IOutputConnector
{
    OutputConnectorType Type { get; }                      // MVP: RestApi. Future: GoogleSheets, Database, Webhook, Sftp, MessageQueue, Plugin
    Task<ConnectorTestResult> TestAsync(OutputConnectorConfig cfg, CancellationToken ct);
    Task<RenderedPayload> RenderAsync(OutputConnectorConfig cfg, DeliveryItem item, CancellationToken ct);   // dry run
    Task<DeliveryResult> DeliverAsync(OutputConnectorConfig cfg, DeliveryItem item, string idempotencyKey,
                                      CancellationToken ct);
}
```

Lifecycle: `Configure → Test/Dry-run → Save → DeliveryRun (scope) → per record: render → deliver (idempotent) → classify → record status`. See doc 12 §7 and decision D15.

### RestApiOutputConnector (MVP)

- **Template engine:** minimal safe mustache subset — `{{record.id}}`, `{{record.externalId}}`, `{{record.metadata.<key>}}`, `{{output.<fieldKey>}}` (dot paths into final output; arrays/objects serialize as JSON), `{{review.isApproved}}`, `{{review.reviewerEmail}}`, `{{review.approvedAt}}`, `{{run.deliveryId}}`. No logic/loops/expressions (injection-proof by construction). Values JSON-encoded per position (string context vs raw JSON via `{{{output.people}}}` triple-stash for raw). Unknown placeholder = config-time validation error.
- `fieldMapping` alternative: flat `{targetField: sourcePath}` object auto-building the body when no template given.
- Batch mode: `mode=batch` renders `{"records": [item, …]}` (or template with `{{items}}`), `batchSize` ≤ 500; one delivery row per record still tracked (batch attempt shared).
- Success classification: status ∈ `successStatusCodes` (default 200/201/202/204); `responseIdPath` extracts external id.
- `behavior: create | upsert` — upsert adds `Idempotency-Key` + admin-specified method/URL pattern with `{{record.externalId}}` in path.

## Connector security summary

- All secrets `protected:` encrypted, masked in reads, replace-only updates, redacted in logs/attempt excerpts (`Authorization: ***`).
- SSRF vetting on every request incl. tests and previews; TLS enforced (dev flag exception); custom headers filtered through allowlist (deny hop-by-hop, `Host`, cookies).
- Response/request excerpts stored ≤4 KB with secret patterns scrubbed.
- Per-connector rate: sequential per connector by default (delivery queue workers=4 across connectors, `DisableConcurrentExecution` per connector id for input sync).
