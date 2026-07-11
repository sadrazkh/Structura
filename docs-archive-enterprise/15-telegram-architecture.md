# 15 — Telegram Architecture

Role boundaries (binding): the bot is **notification + entry point only**. All review work happens in the Mini App (= Reviewer SPA, decision D24). No admin operations via bot.

## Bot lifecycle

- Library: `Telegram.Bot`. Bot token stored encrypted in `app_settings` (`telegram.botToken`, protected), entered in Admin Settings → Telegram; never in code or compose files.
- **Modes** (setting `telegram.mode`):
  - `webhook` (production default): `POST /api/v1/telegram/webhook/{secretPath}` where `secretPath` = random 32-char value generated at config time; additionally validates header `X-Telegram-Bot-Api-Secret-Token` against a second stored secret. "Set Webhook" button calls `setWebhook(url, secret_token, allowed_updates=[message, callback_query])`. Requires public HTTPS base URL.
  - `polling` (dev / restricted networks, R15): `TelegramPollingService` hosted service long-polls `getUpdates` through the configured proxy; webhook deleted automatically on switch.
- Update handling is queued in-process and processed by `TelegramUpdateHandler` (idempotent by `update_id` — last processed id stored; duplicates skipped).
- Outbound bot calls go through SafeHttp `ConnectorEgress` with the Telegram proxy override → global proxy fallback.
- Health: Settings page "Test bot" calls `getMe`; readiness check includes last webhook/polling heartbeat when Telegram is configured.

## Commands

| Command | Behavior |
|---|---|
| `/start` | No payload: greeting + linking instructions. Payload `<code>`: account linking (below). |
| `/tasks` | Linked: per-project pending counts + inline buttons **Open Mini App** (per project) |
| `/next` | Deep link straight to next assigned record: `t.me/<bot>/review?startapp=r_<recordId>` |
| `/progress` | Today/week completed, pending, due soon summary |
| `/open` | Button opening the Mini App root |
| `/help` | Command list + what the bot can/can't do |

Unlinked users receive linking instructions for any command. All texts English. Rate limit: 20 commands/min per Telegram user.

## Account linking (F-link)

1. `POST /telegram/link-codes` (authenticated, rate-limited 5/h): generates 8-char code (Crockford base32, no ambiguous chars), stores **hash** with 10-min expiry; response includes code + deep link `https://t.me/<bot>?start=<code>` + QR.
2. `/start <code>`: hash lookup → must be unexpired, unused; target user must have no active link; the Telegram ID must not be actively linked to another user (else reply "This Telegram account is already linked. Unlink it first.").
3. Success: `telegram_links` row, code marked used, bot confirms, in-app notification + audit `telegram.linked`.
4. Unlink: self-service (`DELETE /telegram/link`) or admin revoke (`POST /users/{id}/revoke-telegram`) → status `Revoked`; subsequent bot/Mini-App access denied instantly (checked per update).
5. Takeover prevention: single-use hashed codes, short expiry, rate limits, binding requires possession of both the app session (code generation) and the Telegram account (sending it), notification on link so a hijacked link is visible, full audit trail.

## Mini App

- BotFather: Mini App configured with URL `https://<host>/tg` (short name `review`).
- Launch: Telegram loads `/tg` → SPA boots with Telegram adapter:
  1. Reads `Telegram.WebApp.initData`.
  2. `POST /auth/telegram-miniapp {initData}` → server validates HMAC-SHA256 against bot token (per Telegram spec), checks `auth_date` ≤ 24 h, resolves active `telegram_links` row → issues the standard JWT pair for that user (scope claim `amr=telegram`).
  3. Router lands on My Tasks; `startapp` payloads: `p_<projectId>` → project queue, `r_<recordId>` → Focus Review of that record (ownership still enforced server-side).
- Theme: Telegram `themeParams` mapped onto design-system CSS variables (`--bg` ← `bg_color`, etc.); `colorScheme` drives light/dark.
- Navigation: Telegram `BackButton` wired to router back; `MainButton` used as Approve in Focus Review (optional enhancement, same handler as the on-screen button).
- Disabled inside Telegram: SW/PWA install, localStorage-persisted refresh tokens longer than session (Mini App uses in-memory tokens + silent re-auth via initData re-validation).
- Unlinked user: `/tg` renders instruction screen (open bot → Settings → link) — no data access.

## Notifications via Telegram

Dispatched by `NotificationDispatchJob` for users with Active links (per-user toggles in settings):

| Event | Recipient | Content |
|---|---|---|
| Assignment created | reviewer | "You have N new records to review in *Project*" + Open Mini App / My tasks buttons |
| Due-date reminder (daily 08:00 installation TZ) | reviewer | overdue + due-today counts |
| Record returned info (after reprocess ready) | reviewer | "A record you returned is ready again" |
| Processing run finished/failed/stopped | run creator + project admins | status, counters, cost |
| Review backlog threshold (configurable, default >500 unassigned) | project admins | backlog size |
| Delivery dead-lettered | project admins | connector + count |

Message formatting: MarkdownV2 with escaping helper; all dynamic strings escaped (no injection via project/record names).

## Error handling

- Telegram API 429 → respect `retry_after`; 5xx → retry ×3; permanent failure → `telegram_status=Failed`, in-app notification stands.
- Webhook handler always returns 200 fast (work queued) to avoid Telegram retry storms; malformed/unauthenticated webhook posts → 404 (no information leak), counted metric for alerting.
- Bot token rotation: replacing token in Settings re-runs `setWebhook` and invalidates old secret path.
