# 22 — Demo & Seed Scenario

Activated with `SEED_DEMO=true` (refused in `ASPNETCORE_ENVIRONMENT=Production` unless `SEED_DEMO_FORCE=true`). Idempotent (keyed on fixed UUIDs). Also serves as the E2E fixture.

## Users

| Email | Password (must change: no, demo) | Role |
|---|---|---|
| `admin@demo.local` | `Demo!Passw0rd` | System Administrator |
| `ops@demo.local` | `Demo!Passw0rd` | Operations Manager on demo project |
| `reviewer1@demo.local` … `reviewer5@demo.local` | `Demo!Passw0rd` | Reviewer on demo project |
| `auditor@demo.local` | `Demo!Passw0rd` | Auditor |

## Provider

`Demo OpenAI-Compatible` — type `OpenAiCompatible`, base URL from env `DEMO_PROVIDER_BASE_URL` (defaults to `https://openrouter.ai/api/v1`; in E2E points at WireMock), key from `DEMO_PROVIDER_API_KEY` (dummy accepted by mock), default model `demo/extractor-small` (mock) or `openai/gpt-4.1-mini` (real), prices seeded ($0.40 / $1.60 per 1M).

## Project: **Incident Reports**

- Schema v1 (published): `firstName` shortText · `lastName` shortText · `incidentType` singleSelect [Theft, Fire, Flood, Assault, Other] · `incidentDate` date (instruction: convert Persian calendar to ISO) · `location` object {city, province} · `description` longText · `priority` singleSelect [Low, Medium, High] · `isUrgent` boolean · `people` objectList {name, role, phone} · `additionalNotes` longText nullable. Confidence threshold 0.7 on `incidentDate`.
- Prompt v1 (published): system instruction for an incident-report extraction assistant; missing → ReturnNull; ambiguous → MarkUnresolved; outputLanguage `en`; strict JSON on; 2 few-shot examples (one Persian, one English source text).
- Review policy: ReviewAll; `allowBulkApprove: true`; budgets: monthly $50, per-run $10.
- 3 test cases with expected outputs (1 Persian, 1 English, 1 mixed with ambiguous date).

## Records

`seed/incident-reports-demo.xlsx` committed under `src/Structura.Persistence/Seed/` — 60 rows (30 Persian, 25 English, 5 messy: empty text, duplicate ID, oversized, ambiguous, injection attempt "ignore previous instructions…" inside text). Seeder imports it through the **real import pipeline** (mapping: `CaseNo`→ID, `ReportText`→text, `Region`,`ReportedAt`→metadata) so the demo exercises production code: 57 imported, 3 row errors visible in the import run.

## Seeded post-import state (executed via real services against the mock provider when `DEMO_PROVIDER_BASE_URL` is WireMock; via stored fixture extraction results otherwise)

- Processing Run #1: 57 records → 54 `Processed`, 2 `ProcessingFailed` (mock-forced timeout), 1 `ValidationFailed` (mock returns bad enum) — demonstrating every failure surface. Tokens/costs recorded.
- Assignment Batch #1: 50 processed records distributed evenly to the 5 reviewers (10 each), priority Normal, due +7 days; notifications created.
- Reviewer1 pre-seeded activity: 6 approved (2 with edited fields → `field_changes` populated), 1 rejected (note "wrong person extracted"), 1 returned for reprocessing, 1 draft saved, 1 untouched.
- Export Run #1: the 6 approved records exported (file present, downloadable).
- Output connector `Demo Webhook Receiver` → `DEMO_OUTPUT_URL` (WireMock in E2E; httpbin-style echo otherwise) + Delivery Run #1: 5 delivered, 1 failed→retried→delivered (attempt history visible).
- Audit log therefore contains a realistic trail; Usage shows run costs; dashboards are non-empty.

## Expected demo walkthrough (README "Try it" section)

1. Sign in as admin → Dashboard populated → open Incident Reports Overview (funnel live).
2. Playground: run the Persian sample → see parsed form + cost.
3. Records: filter `ValidationFailed` → inspect issues → Retry Failed run.
4. Sign in as reviewer1 (or open Mini App if bot configured) → Focus Review the remaining queue → approve/edit/bulk-approve in Table Mode.
5. Back as admin: watch Review Ops update, export approved to Excel (open file: main sheet + `people` child sheet), run API delivery, view attempts, check Audit + Usage.

Telegram: seeding cannot create real links; README documents linking `reviewer1` to a personal Telegram account via the standard flow to receive live notifications.
