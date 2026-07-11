# 10 — Database Design (PostgreSQL)

Conventions: snake_case; PK `id uuid` (UUIDv7); `created_at`/`updated_at timestamptz` on every table (EF interceptor); enums as `text` + CHECK; FKs `ON DELETE RESTRICT` unless noted; `version integer NOT NULL DEFAULT 1` = EF concurrency token where listed. One EF migration lineage in `Structura.Persistence`.

## Tables

### identity
```
users               id, email citext UNIQUE, password_hash, full_name, is_active bool,
                    must_change_password bool, deactivated_at?, last_login_at?, security_stamp
roles               id, name UNIQUE, is_system bool
role_permissions    role_id FK, permission text          UNIQUE(role_id, permission)
user_roles          user_id FK, role_id FK               UNIQUE(user_id, role_id)   -- global roles
refresh_tokens      id, user_id FK, token_hash UNIQUE, expires_at, revoked_at?, replaced_by_id?,
                    created_ip                            IX(user_id)
```

### projects
```
projects            id, name UNIQUE, description, status text CK(Draft|Active|Archived),
                    archived_at?, wizard_state jsonb?,               -- wizard resume (Draft only)
                    ai_provider_id FK?, ai_model text?, generation_settings jsonb,
                    review_policy jsonb, budget_settings jsonb, processing_defaults jsonb,
                    allow_bulk_approve bool DEFAULT false, created_by_id FK, version
project_members     id, project_id FK, user_id FK, role_id FK       UNIQUE(project_id, user_id)
                    IX(user_id)
```

### schemas / prompts
```
schema_versions     id, project_id FK, version_number int, status CK(Draft|Published|Archived),
                    definition jsonb NOT NULL, change_note?, published_at?, published_by_id?,
                    created_by_id, version
                    UNIQUE(project_id, version_number)
                    UNIQUE(project_id) WHERE status='Draft'          -- one draft
prompt_versions     same shape with config jsonb NOT NULL
```
Immutability guard: EF SaveChanges interceptor rejects modification of `definition/config` when status ≠ Draft (defense in depth beyond service logic).

### providers
```
ai_providers        id, name UNIQUE, type CK(OpenRouter|Nvidia|OpenAiCompatible|...future),
                    base_url, api_key_protected text, default_model, available_models jsonb,
                    request_timeout_seconds int, max_retries int, defaults jsonb,
                    concurrency_limit int, requests_per_minute int?, tokens_per_minute int?,
                    custom_headers jsonb, proxy jsonb?, enabled bool, last_test_result jsonb?,
                    version
model_prices        id, ai_provider_id FK?, model text, input_price_per_mtok numeric(12,6),
                    output_price_per_mtok numeric(12,6), source CK(Manual|Synced), updated_by_id?
                    UNIQUE(ai_provider_id, model)
```

### ingestion
```
input_connectors    id, project_id FK, name, config jsonb, schedule_cron text?, enabled bool,
                    sync_checkpoint jsonb?, last_run_at?, version
                    UNIQUE(project_id, name)
import_runs         id, project_id FK, source_type CK(Excel|Csv|Manual|Connector),
                    input_connector_id FK?, file_name?, file_path?, file_size?, sheet_name?,
                    mapping jsonb?, duplicate_policy CK(Skip|FailRow),
                    status CK(Uploaded|AwaitingMapping|Importing|Completed|CompletedWithErrors|
                              Failed|Cancelled|Abandoned),
                    total_rows int?, imported_count int, skipped_duplicate_count int,
                    failed_count int, error jsonb?, created_by_id, started_at?, finished_at?
                    IX(project_id, created_at DESC)
import_row_errors   id, import_run_id FK ON DELETE CASCADE, row_number int, error_code text,
                    message text, raw_excerpt jsonb?     IX(import_run_id)
```

### records
```
records             id, project_id FK, external_id text NOT NULL, input_text text NOT NULL,
                    input_metadata jsonb, source CK(Import|Manual|Connector),
                    import_run_id FK?, input_connector_id FK?,
                    processing_status text CK(...9 values), review_status text CK(...10),
                    delivery_status text CK(...6),
                    latest_extraction_id uuid?,           -- FK added after extraction_results
                    assigned_reviewer_id FK?, priority CK(Low|Normal|High|Urgent) DEFAULT Normal,
                    final_output jsonb?, approved_at?, approved_by_id?, auto_approved bool DEFAULT f,
                    locked_by_id FK?, lock_token uuid?, lock_expires_at?,
                    version int
Indexes:
  UNIQUE(project_id, external_id)
  IX(project_id, processing_status)
  IX(project_id, review_status)
  IX(project_id, delivery_status)
  IX(project_id, assigned_reviewer_id, review_status)     -- reviewer queue
  IX(project_id, import_run_id)
  IX(project_id, updated_at DESC)                         -- keyset pagination (updated_at, id)
  GIN(final_output jsonb_path_ops)                        -- dynamic field filters/export scopes
  IX(lock_expires_at) WHERE lock_expires_at IS NOT NULL   -- lock cleanup
```

### processing
```
processing_runs     id, project_id FK, name, scope jsonb, config_snapshot jsonb,
                    schema_version_id FK, prompt_version_id FK, ai_provider_id FK, model,
                    status CK(Queued|Running|Paused|Cancelling|Cancelled|Completed|
                              CompletedWithErrors|Failed|StoppedByBudget|StoppedByErrorRate),
                    total_tasks int, succeeded int, failed int, cancelled int,
                    estimated_cost numeric, actual_cost numeric, input_tokens bigint,
                    output_tokens bigint, budget_limit numeric?, pause_requested bool,
                    cancel_requested bool, error_rate_threshold numeric?, parent_run_id FK?,
                    created_by_id, started_at?, finished_at?
                    IX(project_id, created_at DESC), IX(status) WHERE status IN ('Queued','Running','Paused')
processing_tasks    id, run_id FK ON DELETE CASCADE, record_id FK, status CK(Queued|Running|
                    Succeeded|Failed|Cancelled|DeadLettered), attempt_count int,
                    last_error_code?, last_error jsonb?, hangfire_job_id?, heartbeat_at?,
                    duration_ms int?
                    UNIQUE(run_id, record_id)
                    IX(run_id, status)
                    IX(status, heartbeat_at) WHERE status='Running'   -- recovery scan
extraction_results  id, record_id FK, run_id FK?, attempt int, schema_version_id FK,
                    prompt_version_id FK, ai_provider_id FK, model,
                    status CK(Succeeded|ValidationFailed|ParseFailed|ProviderFailed),
                    raw_response text?, parsed_output jsonb?, field_meta jsonb?,
                    validation_result jsonb?, input_tokens int, output_tokens int,
                    estimated_cost numeric, duration_ms int, error jsonb?
                    UNIQUE(record_id, run_id, attempt)
                    IX(record_id, created_at DESC), IX(run_id)
                    IX(created_at) WHERE raw_response IS NOT NULL     -- retention cleanup
```
`records.latest_extraction_id` → FK to `extraction_results(id)` DEFERRABLE.

### reviews
```
assignment_batches  id, project_id FK, name, strategy CK(Manual|Even|ByCount|RoundRobin),
                    priority, due_date?, total_count int, created_by_id
review_assignments  id, project_id FK, batch_id FK?, record_id FK, reviewer_id FK,
                    assigned_by_id FK, status CK(Active|Completed|Cancelled|Reassigned),
                    priority, due_date?, started_at?, completed_at?,
                    outcome CK(Approved|Rejected|Returned)?
                    UNIQUE(record_id) WHERE status='Active'
                    IX(reviewer_id, status), IX(batch_id), IX(project_id, status)
record_reviews      id, record_id FK UNIQUE, reviewer_id FK, base_extraction_id FK,
                    draft_output jsonb?, draft_saved_at?, final_output jsonb?,
                    decision CK(Approved|Rejected|Returned)?, note?, field_changes jsonb?,
                    decided_at?, version int
                    GIN(field_changes jsonb_path_ops)                 -- edit-rate queries
review_events       id, record_id FK, assignment_id FK?, actor_id FK?, action text,
                    payload jsonb?, created_at
                    IX(record_id, created_at), IX(actor_id, created_at)
```

### delivery
```
export_runs         id, project_id FK, scope jsonb, column_config jsonb, format CK(Xlsx|Csv),
                    status CK(Queued|Running|Completed|Failed|Cancelled|FileExpired),
                    file_path?, file_size?, row_count int?, error jsonb?, created_by_id,
                    finished_at?, file_expires_at?
output_connectors   id, project_id FK, name, config jsonb, enabled bool, version
                    UNIQUE(project_id, name)
delivery_runs       id, project_id FK, output_connector_id FK, scope jsonb,
                    status CK(Queued|Running|Completed|CompletedWithErrors|Failed|Cancelled),
                    total int, delivered int, failed int, created_by_id, finished_at?
api_deliveries      id, delivery_run_id FK, output_connector_id FK, record_id FK,
                    status CK(Pending|Sending|Delivered|Failed|DeadLettered|Superseded),
                    attempt_count int, external_id?, last_status_code int?, next_retry_at?
                    UNIQUE(output_connector_id, record_id) WHERE status='Delivered'   -- D15
                    IX(delivery_run_id, status), IX(status, next_retry_at) WHERE status='Pending'
delivery_attempts   id, api_delivery_id FK ON DELETE CASCADE, attempt int, status_code int?,
                    request_excerpt text?, response_excerpt text?, error_code?, duration_ms int
```

### playground / telegram / notifications / usage / audit / settings
```
test_cases          id, project_id FK, name, input_text, context_values jsonb?,
                    expected_output jsonb?, created_by_id       UNIQUE(project_id, name)
test_case_runs      id, test_case_id FK ON DELETE CASCADE, schema_version_id, prompt_version_id,
                    ai_provider_id, model, parsed_output jsonb?, validation_result jsonb?,
                    passed bool?, diff jsonb?, input_tokens, output_tokens, estimated_cost,
                    duration_ms, error jsonb?
telegram_links      id, user_id FK UNIQUE, telegram_user_id bigint UNIQUE, telegram_username?,
                    status CK(Active|Revoked), linked_at, revoked_at?, revoked_by_id?
telegram_link_codes id, user_id FK, code_hash text UNIQUE, expires_at, used_at?
                    IX(user_id, created_at)
notifications       id, user_id FK, type text, title, body, data jsonb?, read_at?,
                    telegram_status CK(NotApplicable|Pending|Sent|Failed), created_at
                    IX(user_id, read_at, created_at DESC)
usage_events        id, project_id FK?, ai_provider_id FK, model, source CK(Processing|Playground|TestCase),
                    run_id?, record_id?, input_tokens int, output_tokens int,
                    estimated_cost numeric, created_at
                    IX(project_id, created_at), IX(created_at)
audit_events        id, actor_id FK?, project_id FK?, category text, action text,
                    entity_type text, entity_id uuid?, data jsonb?, correlation_id uuid?,
                    ip inet?, created_at
                    IX(project_id, created_at DESC), IX(actor_id, created_at DESC),
                    IX(category, created_at DESC), IX(correlation_id)
app_settings        key text PK, value text, is_protected bool, updated_by_id?, updated_at
```

## Decision answers (brief §28)

1. **Relational:** everything filtered/joined/counted/transitioned (see D5 rule). 2. **JSONB:** admin-shaped documents (definitions, configs, outputs, metas, snapshots). 3. **Dynamic schema:** one immutable JSONB doc per version (D4). 4. **Extraction result:** JSONB, no FieldResult table (D6). 5. **Hybrid:** yes — exactly per the D5 rule. 6. **Human output:** `record_reviews` JSONB + denormalized `records.final_output` written only in the approve transaction (D7). 7/8. **Schema/Prompt versioning:** sequential immutable published versions + single draft (D8/D9). 9. **Run snapshot:** `config_snapshot` JSONB, secrets excluded (D10). 10. **Dynamic-field indexes:** `GIN(records.final_output jsonb_path_ops)` + `GIN(record_reviews.field_changes)`; queries use `@>` containment; at 50k scale this suffices — expression indexes per hot field are a Phase 2 option. 11. **Audit:** append-only `audit_events`, indexed as above; retention configurable (default: keep forever; cleanup job supports pruning). 12. **Raw AI response retention:** 90 days default (R12), nulled by CleanupJob using the partial index above; token/cost columns survive.

## Other decisions

- **Soft delete:** only `projects.status=Archived` and `users.deactivated_at`. Records hard-delete allowed only in `Imported` state (R22) — FK-safe because nothing references them yet.
- **Migrations:** applied at startup under `pg_advisory_lock(0x53545255)`; `hangfire` schema created by Hangfire itself in the same database.
- **Seeding (idempotent):** roles + permissions, bootstrap admin (F1), default model prices for common OpenRouter/NVIDIA models, demo data only when `SEED_DEMO=true` (see [22-demo-seed-scenario.md](22-demo-seed-scenario.md)).
- **Keyset pagination:** records list orders by `(updated_at DESC, id DESC)` with cursor `(updated_at, id)`; all admin tables ≥1k rows use keyset, small tables use offset.
- **`citext`** extension for email; `uuid` generated app-side (UUIDv7 for index locality).
