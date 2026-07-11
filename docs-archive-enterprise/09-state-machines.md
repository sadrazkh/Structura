# 09 — State Machines

All machines are enforced in backend domain services (`<X>StateMachine.EnsureTransition(from, to)` throws `InvalidStateTransitionException` → HTTP 409 `invalid_state`). UI merely reflects them. Every transition writes an audit event and (where relevant) a SignalR update.

## 1. Record — ProcessingStatus

```
Imported ──enqueue──▶ Queued ──worker──▶ Processing ──ok──▶ Processed
                        │                    │──validation errors──▶ ValidationFailed
                        │                    │──retries exhausted──▶ ProcessingFailed
                        │◀──run cancel/stop── (Queued→Cancelled)
Processed|ValidationFailed|ProcessingFailed|Cancelled ──admin/reviewer──▶ NeedsReprocessing
NeedsReprocessing ──new run──▶ Queued
```

| From \ To | Queued | Processing | Processed | ValidationFailed | ProcessingFailed | NeedsReprocessing | Cancelled |
|---|---|---|---|---|---|---|---|
| Imported | ✅ | — | — | — | — | — | — |
| Queued | — | ✅ | — | — | — | — | ✅ (run cancelled) |
| Processing | ✅ (recovery requeue) | — | ✅ | ✅ | ✅ | — | ✅ |
| Processed | ✅¹ | — | — | — | — | ✅ | — |
| ValidationFailed | ✅ | — | — | — | — | ✅ | — |
| ProcessingFailed | ✅ | — | — | — | — | ✅ | — |
| NeedsReprocessing | ✅ | — | — | — | — | — | — |
| Cancelled | ✅ | — | — | — | — | ✅ | — |

¹ Direct re-queue of `Processed` allowed only via explicit reprocess scopes; if the record is `Approved` on the review axis, requires `includeApproved` (R13) which first resets the review axis.

## 2. Record — ReviewStatus

```
NotReady ──extraction ok──▶ Unassigned ──assign──▶ Assigned ──open──▶ InReview
InReview ──save draft──▶ DraftSaved (⇄ InReview on further edits)
Assigned|InReview|DraftSaved ──approve──▶ Approved
Assigned|InReview|DraftSaved ──reject──▶ Rejected
Assigned|InReview|DraftSaved ──return──▶ ReturnedForReprocessing
Unassigned ──auto-approve policy──▶ Approved (autoApproved=true)
Approved ──admin reprocess override──▶ Unassigned (R13)
Rejected ──reassign──▶ Assigned      Rejected ──reprocess──▶ (see note)
ReturnedForReprocessing ──extraction ok──▶ Assigned (same reviewer, R19) | Unassigned
```

Notes: `QA Required` / `QA Approved` are reserved enum values (accepted in the DB check constraint) with no MVP transitions. When any non-`NotReady` record is reprocessed, on new extraction success: `Rejected → Assigned|Unassigned` (R19 rule), `ValidationFailed`-origin records enter `Unassigned` normally. Unassign: `Assigned|InReview|DraftSaved → Unassigned` (draft preserved). Invalid examples (must 409): `Approved → InReview`, `NotReady → Assigned`, approve without active assignment ownership.

## 3. Record — DeliveryStatus

```
NotReady ──approved──▶ ReadyForExport ──delivery/export starts──▶ Exporting
Exporting ──all target deliveries succeed──▶ Exported
Exporting ──delivery failed──▶ DeliveryFailed ──retry──▶ Exporting
Exported ──new delivery to another connector──▶ Exporting (returns to Exported)
Approved-revoked (reprocess override) ──▶ NotReady
```

Excel export alone also moves `ReadyForExport → Exported` (marked `exportedVia: excel` in audit); API delivery per D15. `DeliveryFailed` never blocks Excel export.

## 4. ProcessingRun

```
Queued ──▶ Running ──▶ Completed | CompletedWithErrors | Failed
Running ──pause──▶ Paused ──resume──▶ Running
Queued|Running|Paused ──cancel──▶ Cancelling ──in-flight drained──▶ Cancelled
Running ──budget breach──▶ StoppedByBudget
Running ──error-rate threshold──▶ StoppedByErrorRate
```

Terminal: `Completed, CompletedWithErrors, Failed, Cancelled, StoppedByBudget, StoppedByErrorRate`. `Failed` = infrastructural failure (e.g., provider config missing), not per-record failures. Pause is cooperative: orchestrator stops dispatching; in-flight tasks finish.

## 5. ImportRun

```
Uploaded ──parse headers/preview──▶ AwaitingMapping ──confirm mapping──▶ Importing
Importing ──▶ Completed | CompletedWithErrors | Failed
Uploaded|AwaitingMapping ──abandon/timeout 24h──▶ Abandoned
Importing ──cancel──▶ Cancelled (rows already imported remain)
```

## 6. ExportRun

```
Queued ──▶ Running ──▶ Completed | Failed
Queued|Running ──cancel──▶ Cancelled (partial file deleted)
Completed ──retention expiry──▶ FileExpired (metadata kept)
```

## 7. DeliveryRun / ApiDelivery

DeliveryRun: `Queued → Running → Completed | CompletedWithErrors | Failed | Cancelled`.

ApiDelivery:
```
Pending ──▶ Sending ──2xx/success code──▶ Delivered (terminal, DB-unique)
Sending ──retryable error──▶ Pending (attempt++, backoff)   [attempts < max]
Sending ──permanent error──▶ Failed
Failed ──manual retry──▶ Pending
Failed ──retries exhausted + admin action or auto──▶ DeadLettered
Delivered/any ──superseded by redeliver──▶ Superseded (old row)
```

Retryable: 408, 429, 5xx, network/timeout. Permanent: other 4xx, payload render errors.

## 8. ReviewAssignment

```
Active ──record decided (approve/reject/return→completed later)──▶ Completed
Active ──unassign/cancel batch──▶ Cancelled
Active ──reassign──▶ Reassigned (new Active row created for new reviewer)
```

`Returned` records keep the assignment `Active` (R19); it completes only on final approve/reject.

## 9. TelegramLink lifecycle

```
(no link) ──generate code──▶ CodeIssued(TelegramLinkCode) ──/start <code>──▶ Active(TelegramLink)
CodeIssued ──10 min──▶ Expired    CodeIssued ──used──▶ consumed (single-use)
Active ──user unlink / admin revoke──▶ Revoked (link row kept for audit; user may re-link)
```

## 10. Record lock (not a status — columns)

Acquire: `lock_expires_at < now()` or same user → set `(locked_by, lock_token, now()+300s)`; else 423 `record_locked`. Heartbeat: token match → extend. Release: token match or admin force (audited). Every review write requires valid token **and** `version` match.

## Enforcement rules (binding)

1. Transitions execute inside DB transactions with `WHERE version = @expected` optimistic guards; concurrent conflicting transition → 409.
2. Status columns have CHECK constraints listing all enum values (including reserved QA values).
3. The three record axes are updated independently but consistently: e.g., approve = single transaction touching ReviewStatus, DeliveryStatus, `final_output`, assignment, review row, audit.
4. Bulk operations apply the machine per record and report per-record failures; they never bypass guards.
5. Background jobs re-read state before acting and treat "already in target state" as success (idempotency).
