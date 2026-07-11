# 03 — Domain Model & Database

## Domain model (simple)

```
User ──< ProjectMember >── Project ──< Record ──< ExtractionResult
                              │           │
                              │           └── final_output (JSONB, human copy)
                              ├──< ImportRun
                              └──< ProcessingRun (snapshots schema+prompt)
User(reviewer) 1──< Record (assigned_reviewer_id)
User 1──1 TelegramLink
```

Project owns its configuration as JSONB documents: `schema_fields`, `prompt_config`, `ai_config`, `api_input_config`, `api_output_config`. No separate schema/prompt/provider/connector entities.

### `schema_fields` JSONB format

```json
{ "version": 3, "fields": [
  { "key": "incidentDate", "label": "Incident Date", "type": "date",
    "required": true, "description": "Date the incident occurred",
    "extractionInstruction": "Convert Persian dates to ISO 8601.",
    "allowedValues": null, "defaultValue": null, "displayOrder": 4 } ] }
```
`type` ∈ `shortText|longText|integer|decimal|boolean|date|singleSelect|multiSelect`; `allowedValues: ["A","B"]` required for selects; key regex `^[a-z][a-zA-Z0-9]{0,63}$`, unique.

## Tables (PostgreSQL, snake_case, all with `id uuid PK, created_at, updated_at`)

```
users             email citext UNIQUE, password_hash, full_name,
                  role text CK(Administrator|ProjectManager|Reviewer),
                  is_active bool, must_change_password bool, last_login_at?

refresh_tokens    user_id FK, token_hash UNIQUE, expires_at, revoked_at?      IX(user_id)

projects          name UNIQUE, description, status CK(Active|Archived),
                  schema_fields jsonb NOT NULL DEFAULT '{"version":0,"fields":[]}',
                  schema_version int NOT NULL DEFAULT 0,
                  prompt_config jsonb, ai_config jsonb,          -- apiKey stored encrypted inside
                  api_input_config jsonb?, api_output_config jsonb?,
                  created_by_id FK

project_members   project_id FK, user_id FK                     UNIQUE(project_id, user_id)

import_runs       project_id FK, source CK(Excel|Csv|Manual|Api), file_name?, file_path?,
                  mapping jsonb?,                                -- {idColumn?, textColumn}
                  status CK(Running|Completed|CompletedWithErrors|Failed|Cancelled),
                  total_rows int?, imported int, skipped_duplicates int, failed int,
                  errors jsonb,                                  -- [{row, message}] capped 1000
                  created_by_id FK, finished_at?

records           project_id FK, external_id text NOT NULL, text text NOT NULL,
                  processing_status CK(Pending|Processing|Completed|Failed) DEFAULT Pending,
                  review_status CK(Unassigned|Assigned|InReview|Approved|Rejected|
                                   ReprocessRequested) DEFAULT Unassigned,
                  delivery_status CK(Pending|Delivered|Failed) DEFAULT Pending,
                  processing_error text?,
                  latest_result_id uuid?,                        -- FK extraction_results, DEFERRABLE
                  processing_run_id FK?, import_run_id FK?,
                  assigned_reviewer_id FK?, assigned_at?,
                  final_output jsonb?,                           -- human working/approved copy
                  reviewed_by_id FK?, reviewed_at?, review_note text?,
                  delivery_attempts int DEFAULT 0, delivered_at?, delivery_error text?,
                  version int NOT NULL DEFAULT 1                 -- optimistic concurrency
    UNIQUE(project_id, external_id)
    IX(project_id, processing_status) · IX(project_id, review_status)
    IX(project_id, assigned_reviewer_id, review_status)          -- reviewer queue
    IX(project_id, updated_at DESC)                              -- keyset paging
    IX(processing_run_id, processing_status)                     -- run progress/claiming

processing_runs   project_id FK, status CK(Running|Completed|CompletedWithErrors|Cancelled|Failed),
                  schema_snapshot jsonb NOT NULL, prompt_snapshot jsonb NOT NULL,
                  model text, total int, succeeded int, failed int,
                  cancel_requested bool DEFAULT false,
                  input_tokens bigint DEFAULT 0, output_tokens bigint DEFAULT 0,
                  created_by_id FK, started_at, finished_at?
    IX(status) WHERE status='Running'                            -- restart recovery scan

extraction_results record_id FK, run_id FK, model text,
                  status CK(Succeeded|Failed),
                  raw_response text?, output jsonb?,             -- AI values, never human-edited
                  error text?, input_tokens int, output_tokens int, duration_ms int
    IX(record_id, created_at DESC) · IX(run_id)

telegram_links    user_id FK UNIQUE, telegram_user_id bigint UNIQUE, telegram_username?,
                  status CK(Active|Revoked)

telegram_link_codes user_id FK, code_hash text UNIQUE, expires_at, used_at?   IX(user_id)

app_settings      key text PK, value text, is_protected bool     -- telegram token, base URL, webhook secret
```

## Storage rules

- Relational: users, projects, members, records (all three statuses + assignment + review + delivery columns), runs. JSONB: schema, prompts, AI/connector configs, AI output, human output.
- AI output (`extraction_results.output`) and human output (`records.final_output`) are separate; approve freezes `final_output` and never touches extraction rows. Raw response kept on the extraction row.
- Secrets inside config JSONB stored as `protected:v1:<base64>` via ASP.NET Data Protection (key ring on `/data/keys`); API responses mask them (`••••1234`); updates are replace-only.
- Concurrency: `records.version` is the single optimistic token (reviewer saves, assignment changes, status transitions all bump it). Runs use atomic counter updates (`UPDATE … SET succeeded = succeeded + 1`).
- Deletes: records deletable only while `processing_status=Pending` and `review_status=Unassigned`; projects archived, never deleted.
- Migrations: EF Core, applied at startup under `pg_advisory_lock`; seeding: bootstrap admin from env + roles are enum (no role table needed).
