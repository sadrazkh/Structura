# 07 — Text Wireframes (Key Screens)

Desktop-width ASCII wireframes. `[Button]`, `(tab)`, `▾ select`, `☐ checkbox`, `⣿` skeleton/live region.

## W1. Admin Dashboard

```
┌ Sidebar ─┐┌──────────────────────────────────────────────────────────────────┐
│ Dashboard││ Dashboard                        [Project: All ▾] [Last 30 days ▾]│
│ Projects ││                                                                  │
│ ReviewOps││ ┌Records──┐ ┌Processing─┐ ┌Review────┐ ┌Cost──────┐              │
│ Reviewers││ │ 48,210  │ │ Running 2 │ │ Backlog  │ │ $214.60  │              │
│ Imports  ││ │ total   │ │ Queued 1k │ │ 3,412    │ │ est $230 │              │
│ Exports  ││ └─────────┘ └───────────┘ └──────────┘ └──────────┘              │
│ Providers││ ┌ Processing volume (day) ────────┐ ┌ Cost by model ───────────┐ │
│ Usage    ││ │ ▂▄▆█▆▄▂ …                       │ │ gpt-4.1-mini ▓▓▓▓ $120   │ │
│ Audit    ││ └─────────────────────────────────┘ └──────────────────────────┘ │
│ Users    ││ Funnel: Imported 12k → Processed 9.4k → Approved 6.1k → Exported │
│ Roles    ││ ┌ Recent runs ───────────────┐ ┌ Recent exports ───────────────┐ │
│ Settings ││ │ Run #12  ▓▓▓▓░ 78%  $41.2  │ │ approved-2026-07-10.xlsx 6.1k │ │
└──────────┘└──────────────────────────────────────────────────────────────────┘
```

## W2. Project Overview

```
Projects / Incident Reports / Overview
┌ Setup checklist ────────────────────────────────────────────────┐
│ ✓ Schema published (v3)   ✓ AI configured (OpenRouter/gpt-4.1)  │
│ ✓ Prompt published (v2)   ○ Add an output destination  [Set up]│
└─────────────────────────────────────────────────────────────────┘
┌ Status funnel ──────────────────────────────────────────────────┐
│ Imported 10,000 → Queued 0 → Processing 0 → Processed 9,412     │
│ Failed 96 · ValidationFailed 492 → Unassigned 2,900 →           │
│ Assigned 4,100 → Approved 2,412 → Exported 0                    │
└─────────────────────────────────────────────────────────────────┘
┌ Active runs ────────────────┐ ┌ Budget ───────────────────────┐
│ Run #12 ▓▓▓▓▓░░ 4,120/5,000 │ │ Month: $86 / $200 ▓▓▓░░ 43%   │
│ [Pause] [Cancel]  $38.20    │ │ Run cap: $50 · Daily: $25     │
└─────────────────────────────┘ └───────────────────────────────┘
```

## W3. Schema Builder

```
Projects / Incident Reports / Schema (Draft v4)          [Save Draft] [Publish…]
┌ Fields ──────────────┐┌ Field editor: incidentDate ─────────┐┌ Preview ──────┐
│ ⋮⋮ firstName   text  ││ General                             ││ (JSON)(Form)  │
│ ⋮⋮ lastName    text  ││  Key   incidentDate                 ││ {             │
│ ⋮⋮ incidentType sel ●││  Label Incident Date                ││  "firstName": │
│ ⋮⋮ incidentDate date ││  Type  Date ▾   ☑ Required ☐ Null   ││   "string",   │
│ ⋮⋮ location   object ││ Extraction                          ││  "incident…   │
│   ⋮⋮ city      text  ││  Instruction: "Extract the date…"   ││ }             │
│   ⋮⋮ province  text  ││  Examples: 2024-05-01               ││               │
│ ⋮⋮ people  list<obj> ││ Validation                          ││               │
│   ⋮⋮ name / role / … ││  Min  — Max — Regex —               ││               │
│ ⋮⋮ isUrgent   bool   ││ Review                              ││               │
│ [+ Add field ▾]      ││  Confidence threshold 0.7           ││               │
│ [+ Person][+Location]││  ☑ Editable ☐ Hidden                ││               │
└──────────────────────┘│ Export: mode Auto ▾  header —       │└───────────────┘
                        └─────────────────────────────────────┘
```

## W4. AI Configuration

```
Projects / Incident Reports / AI Configuration                       [Save]
Provider      [OpenRouter (openrouter) ▾]     status: ● enabled, tested 2h ago
Model         [openai/gpt-4.1-mini ▾ or type…]   ☑ supports structured output
Generation    Temperature [0.1]  Top P [1.0]  Max output tokens [2048]
Limits        Concurrency override [8]   Request timeout [60s]
              [Test with this model]  → ✓ ok · 412ms · 21 tokens
⚠ Changing the model does not affect runs already in progress.
```

## W5. Playground

```
Projects / Incident Reports / Playground
┌ Input ────────────────────────────┐┌ Result ─────────────────────────────────┐
│ Sample text                       ││ (Form)(JSON)(Raw)(Prompt)(Validation)   │
│ ┌───────────────────────────────┐ ││ ┌─────────────────────────────────────┐ │
│ │ در تاریخ ۱۲ مرداد …           │ ││ │ First Name  [Sara      ] ●0.92      │ │
│ └───────────────────────────────┘ ││ │ Incident    [Theft ▾   ] ●0.81      │ │
│ Context: reportDate [2026-05-02]  ││ │ Date        [2026-08-03] ⚠ low 0.44 │ │
│ Provider [OpenRouter ▾]           ││ └─────────────────────────────────────┘ │
│ Model    [gpt-4.1-mini ▾]         ││ tokens 1,204 in / 312 out · $0.0031 ·  │
│ Schema   [v3 (published) ▾]       ││ 2.4s                                    │
│ Prompt   [v2 (published) ▾]       ││                                         │
│ [▶ Run Test] [Save as Test Case]  ││                                         │
└───────────────────────────────────┘└─────────────────────────────────────────┘
```

## W6. Excel Import — Mapping

```
Import file: reports-2026.xlsx (8.4 MB · sheet "Data" ▾ · ☑ first row is header)
┌ Preview (first 50 rows) ────────────────────────────────────────────────┐
│ A: CaseNo   B: ReportText              C: Region   D: ReportedAt        │
│ 1001        در تاریخ …                 East        2026-01-04           │
│ 1002        The customer reported …    West        2026-01-05           │
└─────────────────────────────────────────────────────────────────────────┘
Mapping   Record ID  [A: CaseNo ▾]     ⚠ 2 duplicate IDs · 0 empty
          Text       [B: ReportText ▾] ⚠ 1 empty text row
          Metadata   [☑ C: Region  ☑ D: ReportedAt]
          Context    [☑ D: ReportedAt]
Duplicates [Skip duplicates ▾]        [Cancel] [Start Import → 9,998 rows]
```

## W7. API Input Connector Builder

```
Connector: CityDesk API                       [Test Connection] [Save] [Run Now]
┌ Connection ──────────────────────────────────────────────────────────────┐
│ Base URL [https://api.citydesk.example]  Endpoint [/v2/reports] GET ▾    │
│ Auth [Bearer token ▾] Token [••••1234 Replace]  Timeout [30s] Proxy [—]  │
├ Pagination ──────────────────────────────────────────────────────────────┤
│ Type [Cursor ▾]  Page size [200]  Cursor path [$.meta.next]              │
├ Mapping ─────────────────────────────────────────────────────────────────┤
│ Data array [$.data[*]]  ID [$.id]  Text [$.body]                         │
│ Metadata: region ← $.region · reportedAt ← $.created_at                  │
│ Incremental sync field [$.created_at]  since [last checkpoint]           │
├ Schedule ────────────────────────────────────────────────────────────────┤
│ [Every 6 hours ▾]  ● enabled                                             │
└──────────────────────────────────────────────────────────────────────────┘
[Preview Response] → raw JSON   [Preview Mapped Records] → 200 rows, 3 dup
```

## W8. Processing Run Details

```
Run #12 — 5,000 records            ● Running   [Pause] [Cancel] [Retry Failed]
▓▓▓▓▓▓▓▓▓▓▓▓░░░░░ 4,120 / 5,000 (82%) · 96 failed · 310/min · ETA 3m
┌ Cost ───────────────────────────┐ ┌ Snapshot ────────────────────────────┐
│ 4.1M in / 0.9M out tokens       │ │ Schema v3 · Prompt v2                │
│ $38.20 actual · $41 estimated   │ │ OpenRouter · gpt-4.1-mini · temp 0.1 │
│ Run budget $50 ▓▓▓▓▓▓▓░ 76%     │ │ Concurrency 8 · Retries 3            │
└─────────────────────────────────┘ └──────────────────────────────────────┘
Errors: provider_timeout ×41 · invalid_json ×38 · rate_limited ×17  [expand]
┌ Tasks (filter: Failed ▾) ────────────────────────────────────────────────┐
│ #1042  Failed  3 attempts  provider_timeout   12.1s   [view record]      │
└──────────────────────────────────────────────────────────────────────────┘
```

## W9. Assignment Manager

```
Assignments                                          [+ New Assignment Batch]
┌ New batch ──────────────────────────────────────────────────────────────┐
│ Scope [review_status = Unassigned ▾ + filters]  → 500 records           │
│ Reviewers [☑ Sara ☑ Omid ☑ Mina ☑ Reza ☑ Neda]                          │
│ Strategy [Distribute evenly ▾]  Priority [Normal ▾]  Due [2026-07-18]   │
│ Preview: 100 each                                  [Cancel] [Assign 500] │
└─────────────────────────────────────────────────────────────────────────┘
Batches: #8 · 500 recs · 5 reviewers · 212 done ▓▓▓░░  [Reassign remainder]
┌ Assignments (batch #8, status Active ▾) ────────────────────────────────┐
│ ☐ 1042  Sara  Active  P:High  due 07-18  started —   [Reassign ▾]       │
└─────────────────────────────────────────────────────────────────────────┘
```

## W10. Review Operations Dashboard

```
Review Operations                                  [Project: Incident ▾]
Backlog 3,412 · Unassigned 2,900 · Oldest pending 3d · Completed today 212
┌ Per reviewer ────────────────────────────────────────────────────────────┐
│ Reviewer  Pending  Today  Avg time  Edit rate  Reject %  TG   actions    │
│ Sara      88       41     1m 32s    34%        2%        ✓   [view]      │
│ Omid      100      12     3m 10s    41%        6%        ✗   [view]      │
└──────────────────────────────────────────────────────────────────────────┘
ⓘ Throughput depends on record difficulty — do not compare reviewers naively.
```

## W11. Focus Review (Reviewer, desktop)

```
Incident Reports — Review                     12 / 100  ▓▓░░░░░░  [Table view]
┌ Original text ────────────────────┐┌ Extracted data (v3) ─────────────────┐
│ (dir=auto)                        ││ Person                               │
│ در تاریخ ۱۲ مرداد خانم سارا …     ││  First Name [Sara     ] ●0.92 ↩      │
│ …evidence highlight…              ││  Last Name  [Ahmadi   ] ●0.88        │
│                                   ││ Incident                             │
│ ▸ Metadata (Region: East, …)      ││  Type [Theft ▾] ●0.81                │
│ ▸ Context                         ││  Date [2026-08-03] ⚠ 0.44 low        │
│                                   ││   ⓘ evidence: "۱۲ مرداد…"            │
│                                   ││  ☑ Is Urgent ●0.90                   │
│                                   ││ People (2 items) [+ add]             │
│                                   ││  ┌ 1. name/role/phone … ┐            │
│                                   ││ Validation: 1 warning ▾              │
└───────────────────────────────────┘└──────────────────────────────────────┘
[Save Draft]  [Return ↺]  [Reject ✕]            [← Prev] [Approve ✓ (A)] [→]
```

## W12. Table Review (Reviewer)

```
Incident Reports — Table Review        [Columns ▾] [Filter: Assigned ▾]
┌──────────────────────────────────────────────────────────────────────────┐
│☐  ID    First name  Type▾    Date        People  ⚠  ●     status        │
│☐ 1041  [Sara     ] [Theft ▾] [2026-08-03] 2 items ⚠1 0.44  DraftSaved   │
│☑ 1042  [Omid     ] [Fire  ▾] [2026-08-04] {…}    —  0.91  Assigned      │
│☑ 1043  [Mina     ] [Flood ▾] [2026-08-04] 1 item  —  0.88  Assigned     │
└──────────────────────────────────────────────────────────────────────────┘
2 selected   [Bulk Approve ✓] [Bulk Reject] [Bulk Return]      1–50 of 100
```

## W13. Export Mapping (Excel)

```
New Export — Incident Reports
Scope [Approved ▾] [date range —]  → 6,112 records
┌ Columns (drag to order) ───────────────┐┌ Included ─────────────────────┐
│ Record: ☑ External ID ☑ Imported at    ││ 1 External ID                 │
│ Fields: ☑ firstName ☑ lastName         ││ 2 firstName …                 │
│  ☑ incidentType ☑ incidentDate         ││ 9 Reviewer · 10 Review date   │
│  ☑ location.* (flatten) ☑ people →     ││ Sheet "people": Record ID,    │
│    child sheet                         ││  Item Index, name, role, phone│
│ Meta: ☑ Reviewer ☑ Review date ☑ Model ││                               │
└────────────────────────────────────────┘└───────────────────────────────┘
Format [.xlsx ▾]     [Save as default template]   [Cancel] [Start Export]
```

## W14. Usage & Costs

```
Usage & Costs                       [Range: This month ▾] [Project: All ▾]
Tokens 41.2M in / 8.4M out · Estimated $214.60 · Actual $209.11
┌ Cost by day ▁▂▄█▄▂ ┐ ┌ By model ─────────────┐ ┌ By source ─────────────┐
│                    │ │ gpt-4.1-mini  $120.20 │ │ Processing 92%         │
│                    │ │ llama-3.3-70b  $61.40 │ │ Playground 6% · Test 2%│
└────────────────────┘ └───────────────────────┘ └─────────────────────────┘
Budgets: Incident Reports $86/$200 ▓▓▓░░ · CityDesk $12/$50 ▓▓░░░
[Model price table →]   Cost per approved record: $0.034
```
