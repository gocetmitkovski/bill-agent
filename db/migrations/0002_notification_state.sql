-- 0002_notification_state.sql
--
-- Day 10 (Telegram, post-build): de-duplicate outbound notifications.
--
-- Problem: ReconcilerAgent.SweepAsync iterates every payment with
-- matched_bill_id IS NULL on every tick. For "matched" outcomes this is fine —
-- the payment leaves the unmatched set the moment Agent B succeeds, so its
-- Telegram notification fires once. For "ambiguous" (needs_review) and
-- "unmatched" outcomes nothing on the payment row changes, so the payment
-- stays in the sweep set, the agent re-decides the same outcome every tick,
-- and the user gets a duplicate Telegram message every tick.
--
-- Fix: record the last notification outcome on the payment row. The notifier
-- short-circuits when the about-to-send status equals the already-sent status.
-- A transition (e.g. ambiguous -> matched once the invoice arrives) still
-- notifies, because the stored value differs.
--
-- NOTE: 0001_init.sql now declares this column inline, so a fresh
--   docker compose down -v && docker compose up -d
-- produces it from scratch. This migration exists for EXISTING databases that
-- were created before the column was added. Apply with:
--   docker compose cp db/migrations/0002_notification_state.sql postgres:/tmp/0002.sql
--   docker compose exec postgres psql -U billagent -d billagent -f /tmp/0002.sql

ALTER TABLE payments
    ADD COLUMN IF NOT EXISTS last_notified_outcome TEXT;

-- Backfill: payments already matched were notified as "Matched" on the run that
-- matched them. Mark them so a restart's first sweep doesn't re-notify. Unmatched
-- and ambiguous payments are left NULL so they get exactly one fresh notification
-- after the migration — the signal that de-dup is now active.
UPDATE payments
   SET last_notified_outcome = 'Matched'
 WHERE matched_bill_id IS NOT NULL
   AND last_notified_outcome IS NULL;
