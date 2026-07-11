# 08 — Domain Model

## Aggregate map

| Aggregate root | Contains / owns | Module |
|---|---|---|
| `User` | `RefreshToken`, global role links | Identity |
| `Role` | permission keys | Identity |
| `Project` | `ProjectMember`, settings (review policy, budgets, AI binding) | Projects |
| `SchemaVersion` | immutable `SchemaDefinition` document | Schemas |
| `PromptVersion` | immutable prompt config document | Prompts |
| `AiProvider` | connection/limits config, encrypted key | Providers |
| `ModelPrice` | — | Providers |
| `InputConnector` | config, sync checkpoint | Ingestion |
| `ImportRun` | `ImportRowError` | Ingestion |
| `Record` | statuses, lock, denormalized final output | Records |
| `ExtractionResult` | parsed output, field meta, raw response | Processing |
| `ProcessingRun` | `ProcessingTask`, config snapshot | Processing |
| `AssignmentBatch` | — | Reviews |
| `ReviewAssignment` | — | Reviews |
| `RecordReview` | draft/final output, field changes | Reviews |
| `ReviewEvent` | append-only | Reviews |
| `ExportRun` | column config, file ref | Delivery |
| `OutputConnector` | config | Delivery |
| `DeliveryRun` | `ApiDelivery` → `DeliveryAttempt` | Delivery |
| `TestCase` | `TestCaseRun` | Prompts (Playground) |
| `TelegramLink`, `TelegramLinkCode` | — | Telegram |
| `Notification` | — | Notifications |
| `UsageEvent` | append-only | Usage |
| `AuditEvent` | append-only | Audit |
| `AppSetting` | key/value (encrypted-capable) | SharedKernel/Settings |

Changes vs the brief's entity list (rationale):
- `SchemaDefinition`/`PromptDefinition` dropped as separate entities — a project has exactly one schema/prompt lineage, so versions hang directly off `Project` (simpler, no empty indirection).
- `AIModelConfiguration` folded into `Project` (columns `ai_provider_id`, `ai_model`, `generation_settings`) — per-project binding, no reuse case in MVP.
- `RecordInput` folded into `Record` (`input_text`, `input_metadata`) — 1:1 always.
- `Review` split into `RecordReview` (state) + `ReviewEvent` (history) — cleaner metrics and audit.
- `ProcessingJob` renamed `ProcessingTask` (per-record) to avoid confusion with Hangfire jobs.
- `ImportMapping` stored on `ImportRun.mapping` (snapshot) and defaulted from the last run — no standalone entity.
- Added: `AssignmentBatch`, `DeliveryRun`, `DeliveryAttempt`, `ImportRowError`, `ModelPrice`, `TelegramLinkCode`, `TestCaseRun`, `RefreshToken`, `AppSetting`.

## Key entities (properties abbreviated; full columns in [10-database-design.md](10-database-design.md))

**Record** — the pivot of the whole system.
- Identity: `Id`, `ProjectId`, `ExternalId` (unique per project, auto-generated `REC-xxxxxx` when absent).
- Input: `InputText`, `InputMetadata` (JSONB dict), `Source` (`Import|Manual|Connector`), `ImportRunId?`, `InputConnectorId?`.
- State: `ProcessingStatus`, `ReviewStatus`, `DeliveryStatus` (three independent machines), `LatestExtractionId?`, `AssignedReviewerId?`, `Priority` (`Low|Normal|High|Urgent`).
- Output: `FinalOutput` (JSONB, denormalized on approve), `ApprovedAt?`, `ApprovedById?` (null = auto-approved), `AutoApproved` flag.
- Locking/concurrency: `LockedById?`, `LockToken?`, `LockExpiresAt?`, `Version` (int, optimistic token).

**SchemaVersion**
- `ProjectId`, `VersionNumber`, `Status` (`Draft|Published|Archived`), `Definition` (JSONB, format below), `PublishedAt?`, `PublishedById?`, `ChangeNote`.
- Invariants: one Draft per project; Published immutable; VersionNumber sequential.

**PromptVersion**
- Same lifecycle. `Config` JSONB: `{systemInstruction, generalInstruction, inputTemplate, contextFields[], outputLanguage, missingValueBehavior, ambiguousValueBehavior, unknownValueBehavior, strictJson, fewShotExamples[{inputText, contextValues?, expectedOutput}], referenceContext}`.

**AiProvider**
- `Name`, `Type` (`OpenRouter|Nvidia|OpenAiCompatible` — future: `OpenAi|Anthropic|Gemini|AzureOpenAi|Ollama`), `BaseUrl`, `ApiKeyProtected`, `DefaultModel`, `AvailableModels` (JSONB `[{id, supportsStructuredOutput}]`), `RequestTimeoutSeconds`, `MaxRetries`, `Defaults` (temperature/topP/maxOutputTokens), `ConcurrencyLimit`, `RequestsPerMinute`, `TokensPerMinute`, `CustomHeaders` (JSONB, values protected), `Proxy` (JSONB `{url, type, usernameProtected?, passwordProtected?}`), `Enabled`, `LastTestResult` (JSONB).

**ProcessingRun**
- `ProjectId`, `Name`, `Scope` (JSONB filter snapshot + explicit record IDs when selected), `ConfigSnapshot` (JSONB per D10), `SchemaVersionId`, `PromptVersionId`, `AiProviderId`, `Model`, `Status`, counters (`TotalTasks`, `Succeeded`, `Failed`, `Cancelled`), `EstimatedCost`, `ActualCost`, `InputTokens`, `OutputTokens`, `BudgetLimit?`, `PauseRequested`, `CancelRequested`, `CreatedById`, `StartedAt/FinishedAt`, `ParentRunId?` (retry lineage).

**ProcessingTask**
- `RunId`, `RecordId` (unique together), `Status`, `AttemptCount`, `LastErrorCode?`, `LastErrorDetail?` (JSONB), `HangfireJobId?`, `HeartbeatAt?`, `DurationMs?`.

**ExtractionResult**
- `RecordId`, `RunId?` (null for Playground-origin), `Attempt`, `SchemaVersionId`, `PromptVersionId`, `AiProviderId`, `Model`, `Status` (`Succeeded|ValidationFailed|ParseFailed|ProviderFailed`), `RawResponse?` (retention-limited), `ParsedOutput?` (JSONB), `FieldMeta?` (JSONB `{fieldKey: {confidence?, evidence?}}`), `ValidationResult?` (JSONB `{errors[], warnings[]}` each `{fieldKey, code, message}`), `InputTokens`, `OutputTokens`, `EstimatedCost`, `DurationMs`, `Error?` (JSONB).

**ReviewAssignment** — `ProjectId`, `RecordId`, `ReviewerId`, `BatchId?`, `AssignedById`, `Status` (`Active|Completed|Cancelled|Reassigned`), `Priority`, `DueDate?`, `StartedAt?`, `CompletedAt?`, `Outcome?` (`Approved|Rejected|Returned`). Partial unique: one Active per record.

**RecordReview** — `RecordId` (unique), `ReviewerId`, `DraftOutput?` (JSONB), `DraftSavedAt?`, `FinalOutput?` (JSONB), `Decision?`, `Note?`, `FieldChanges?` (JSONB per D18), `DecidedAt?`, `BaseExtractionId`, `Version`.

**OutputConnector** — `ProjectId`, `Name`, `Config` JSONB: `{url, method, headers{}, auth{type, credentialsProtected}, bodyTemplate, fieldMapping{}, mode: single|batch, batchSize, successStatusCodes[], responseIdPath, timeoutSeconds, retryPolicy{maxAttempts, backoffSeconds[]}, behavior: create|upsert, proxy?}`, `Enabled`.

**ApiDelivery** — `DeliveryRunId`, `OutputConnectorId`, `RecordId`, `IdempotencyKey` (= own Id), `Status` (`Pending|Sending|Delivered|Failed|DeadLettered|Superseded`), `AttemptCount`, `ExternalId?`, `LastStatusCode?`. Child `DeliveryAttempt`: `RequestExcerpt`, `ResponseExcerpt` (≤4 KB each), `StatusCode?`, `ErrorCode?`, `DurationMs`, `CreatedAt`.

**TelegramLink** — `UserId` (unique), `TelegramUserId` (unique), `TelegramUsername?`, `LinkedAt`, `Status` (`Active|Revoked`), `RevokedById?`. `TelegramLinkCode` — `CodeHash`, `UserId`, `ExpiresAt`, `UsedAt?`.

**UsageEvent** — `ProjectId?`, `AiProviderId`, `Model`, `Source` (`Processing|Playground|TestCase`), `RunId?`, `RecordId?`, `InputTokens`, `OutputTokens`, `EstimatedCost`, `CreatedAt`.

**AuditEvent** — `ActorId?` (`null` = system), `ProjectId?`, `Category` (`identity|projects|schemas|prompts|providers|ingestion|records|processing|reviews|assignments|delivery|telegram|settings`), `Action` (e.g. `record.approved`), `EntityType`, `EntityId`, `Data` (JSONB: compact before/after or parameters — never secrets/full record text), `CorrelationId`, `Ip?`, `CreatedAt`.

## Value objects

- `FieldSpec` (below), `GenerationSettings` {temperature, topP, maxOutputTokens}, `RetryPolicy` {maxAttempts, backoffSeconds[]}, `ProxyConfig`, `ReviewPolicy` (D20), `BudgetSettings` {projectTotal?, daily?, monthly?, perRun?, warningPercent}, `ValidationIssue` {fieldKey, code, message, severity}, `TokenUsage` {input, output}, `ScopeFilter` (record filter snapshot).

## Schema Definition JSONB format (canonical)

```json
{
  "formatVersion": 1,
  "fields": [
    {
      "key": "incidentDate",
      "label": "Incident Date",
      "description": "Date the incident occurred",
      "type": "date",
      "required": true,
      "nullable": true,
      "defaultValue": null,
      "extractionInstruction": "Convert Persian calendar dates to Gregorian ISO 8601.",
      "examples": ["2026-08-03"],
      "allowedValues": null,
      "validation": { "min": null, "max": null, "minLength": null, "maxLength": null, "regex": null, "minItems": null, "maxItems": null },
      "confidenceThreshold": 0.7,
      "requiresReview": false,
      "hidden": false,
      "readOnly": false,
      "order": 4,
      "group": "Incident",
      "dependsOn": null,
      "export": { "mode": "auto", "header": null },
      "children": null,
      "itemType": null
    }
  ]
}
```

Rules:
- `type` ∈ `shortText, longText, integer, decimal, boolean, date, dateTime, singleSelect, multiSelect, email, phone, url, tags, stringList, objectList, object, keyValue, json` (R7: `enum`→`singleSelect`; `person`/`location` are builder templates producing `object`/`objectList` structures).
- `children` required for `object`/`objectList` (recursion, max depth 4); `itemType` unused otherwise.
- `allowedValues: [{value, label}]` for `singleSelect`/`multiSelect`.
- `dependsOn: {fieldKey, operator: "equals|notEquals|isEmpty|isNotEmpty", value}` — controls form visibility only (not extraction).
- `export.mode` ∈ `auto | json | flatten | sheet | join` (D23 defaults apply when `auto`).
- Reserved keys: anything starting with `_`. Key regex: `^[a-z][a-zA-Z0-9]{0,63}$`, unique per nesting level.
- Limits: ≤150 fields total, depth ≤4, ≤100 allowedValues.

## Extraction output envelope (what the model must return)

```json
{
  "data":  { "<fieldKey>": "<value>", "...": "..." },
  "meta":  { "<fieldKey>": { "confidence": 0.92, "evidence": "short quote from source" } }
}
```

`data` conforms to the generated JSON Schema of the schema version; `meta` is optional per field. Nested field meta keys use dot paths (`location.city`, `people[1].name`).
