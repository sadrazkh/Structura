# 06 — Telegram Integration

Scope: **notifications + entry point only.** No review forms in chat, no admin operations via bot.

## Bot setup

- Library: `Telegram.Bot`. Token entered in Admin Settings, stored encrypted (`app_settings`), masked in UI.
- Modes (setting): **webhook** (production; `POST /api/telegram/webhook/{secret}` — random 32-char path + `X-Telegram-Bot-Api-Secret-Token` check; "Set Webhook" button) or **polling** (dev / restricted networks; hosted service). Outbound calls honor the global proxy env var.
- Update handling idempotent by `update_id` (last processed id stored in `app_settings`).

## Commands

| Command | Behavior |
|---|---|
| `/start` | greeting + link instructions; `/start <code>` performs account linking |
| `/tasks` | pending count per project + **Open App** web-app button |
| `/next` | button deep-linking to the next assigned record (`startapp=r_<recordId>`) |
| `/help` | short command list |

Unlinked users always get linking instructions. All bot text English; MarkdownV2 with an escaping helper (project names may contain any characters).

## Account linking

1. Reviewer (any user) → Settings → **Generate Code**: 8-char code, stored **hashed**, 10-minute expiry, single-use, rate-limited (5/hour).
2. User sends `/start <code>` (deep link `t.me/<bot>?start=<code>` shown as button + QR).
3. Bot validates (exists, unexpired, unused; Telegram ID not already actively linked to another user) → creates `telegram_links` row → confirms in chat; app Settings shows "Linked to @username".
4. Unlink: from app Settings; Administrator can revoke from Users page. Revoked link = bot ignores user, Mini App auth fails.

## Mini App

- BotFather Mini App URL: `https://<host>/tg` (short name e.g. `review`). `/tg` serves the same Reviewer SPA with a small adapter:
  1. Reads `Telegram.WebApp.initData` → `POST /auth/telegram-miniapp` → server validates HMAC-SHA256 with the bot token + `auth_date ≤ 24h` + active link → issues normal JWT.
  2. Maps Telegram theme params to CSS variables (dark/light); wires Telegram BackButton to router back.
  3. `startapp` payloads: `p_<projectId>` → project record list, `r_<recordId>` → Focus Review (ownership re-checked server-side).
- Unlinked Telegram user → instruction screen. PWA install prompts disabled inside Telegram.

## Notifications (sent by the app right after the triggering action; retry ×2 on failure, then logged)

| Trigger | Recipient | Message |
|---|---|---|
| Records assigned | reviewer (linked) | "📋 You have N new records to review in *Project*." + Open App / Next buttons |
| Reprocessed record ready again | reviewer | "↺ A record you sent for reprocessing is ready." |
| Processing run finished | run creator (linked) | "✅ Run finished: X succeeded, Y failed." |

No notification tables/center in V1 — Telegram only; unlinked users simply see counts in the app.
