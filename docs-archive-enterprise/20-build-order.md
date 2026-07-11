# 20 — Recommended Build Order

Strict sequence; a task starts only when its dependencies are complete. Tasks reference doc 19. Items on the same line may proceed in parallel where team/agent capacity allows.

```
Stage 0  Foundation
  1. T0.1  Solution & module skeleton
  2. T0.2  Persistence foundation
  3. T0.3  SharedKernel primitives

Stage 1  Identity
  4. T1.1  Users/roles/permissions + bootstrap
  5. T1.2  JWT auth + refresh rotation
  6. T1.3  Authorization pipeline (+ authz matrix test harness)
  7. T1.5  Sign-in + app shell + design system foundation
  8. T1.4  Users & Roles management

Stage 2  Projects & security core
  9. T2.1  Project CRUD + members
 10. T13.1 SafeHttpClientFactory (SSRF suite)          ← early: consumed by E4/E8/E16/E17

Stage 3  Configuration plane
 11. T3.1  FieldSpec model + validation
 12. T3.2  Schema version lifecycle
 13. T4.1  Provider entity + secrets        ∥  T5.1 Prompt version lifecycle
 14. T4.2  OpenAiCompatible adapter + RateGate
 15. T5.2  PromptBuilder
 16. T6.1  JSON Schema generation
 17. T6.2  Parser + repair                  ∥  T6.3 SchemaOutputValidator (+ shared vectors)
 18. T6.4  ExtractionService
 19. T3.3  Schema Builder UI                ∥  T4.3 Providers UI  ∥  T5.3 Prompt UI

Stage 4  Prove the pipeline
 20. T10.1 Dynamic form renderer            (T10.2 TS validation ∥ T10.3 review chrome)
 21. T7.1  Playground (endpoint + UI)
 22. T7.2  Test cases
 23. T2.2/T2.3 Create Project Wizard (full integration)

Stage 5  Data in
 24. T8.1  Upload + preview + mapping
 25. T8.2  FileImportJob
 26. T9.1  Record store + filters + Records UI
 27. T8.3  Manual input
 28. T8.4  REST input connector

Stage 6  Bulk processing
 29. T11.1 Run creation + estimation
 30. T19.1 SignalR hub + LiveQuery fallback           ← before live progress UIs
 31. T11.2 Orchestrator + extraction job + recovery
 32. T11.3 Processing UI
 33. T12.1 Review policy routing + project policy UI

Stage 7  Human review
 34. T15.1 Locking + heartbeat
 35. T15.2 Review write endpoints (draft/approve/reject/return/bulk)
 36. T14.1 Assignment batches
 37. T14.2 Assignment mutations
 38. T15.3 My Tasks + Review Queue UI
 39. T15.4 Focus Review UI
 40. T15.5 Table Review UI
 41. T15.6 Completed + Progress
 42. T14.3 Review Operations dashboards

Stage 8  Notifications & Telegram
 43. T18.4 Notification center
 44. T16.1 Bot core + settings
 45. T16.2 Account linking
 46. T16.3 Commands + notifications
 47. T16.4 Mini App auth + adapter

Stage 9  Data out
 48. T17.1 Excel export
 49. T17.2 Output connector + template engine
 50. T17.3 Delivery runs (idempotent, retries, dead-letter)

Stage 10 Visibility
 51. T18.1 Audit query/UI   ∥  T18.2 Usage & costs
 52. T18.3 Dashboard + Project Overview completion

Stage 11 PWA
 53. T20.1 PWA shell
 54. T20.2 Offline drafts + sync

Stage 12 Ship
 55. T21.1 Docker & compose
 56. T21.3 Demo seed
 57. T21.2 Ops runbook
 58. T22.1 E2E suite → T22.2 security pack → T22.3 perf sanity → T22.4 UX acceptance
```

Rationale highlights: SafeHttp lands before anything that makes outbound calls; the extraction pipeline is proven interactively (Playground) before bulk machinery; SignalR precedes live-progress UIs; review endpoints precede reviewer UI; Telegram waits for notifications+review to exist; PWA/offline last because it wraps a stable reviewer app; the E2E gate runs against the real compose stack.
