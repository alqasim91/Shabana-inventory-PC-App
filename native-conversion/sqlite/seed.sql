-- ============================================================================
-- seed.sql — MINIMAL first-run data for the native (SQLite) edition.
-- Run once, right after schema.sql. Idempotent (safe to re-run).
--
-- Deliberately contains NO demo data and NO real password:
--   * one default branch (the schema works with a single site — rule 1)
--   * the business-identity row (values previously hard-coded in labels.ts, 0009)
--   * ONE placeholder admin whose password_hash is NULL and
--     must_change_password = 1. The app's first-run screen must detect
--     "an admin with password_hash IS NULL" and ask for the new password, hash it
--     (Argon2id; PBKDF2-SHA256 fallback), UPDATE users SET password_hash = ...,
--     must_change_password = 0, password_changed_at = <now>, then set
--     app_meta.first_run_completed = '1'. Login MUST refuse any user whose
--     password_hash IS NULL outside that one first-run flow.
--   * the 8 default expense categories (0042)
-- Reference data (permission_keys, role_presets, app_meta.schema_version) is
-- already inserted by schema.sql because the app cannot run without it.
-- The Postgres demo seed (supabase/seed.sql) is NOT ported: see the notes §8.
-- ============================================================================
PRAGMA foreign_keys = ON;

INSERT OR IGNORE INTO business_settings (id, business_name, address_line, phone_line, currency, timezone)
VALUES (1, 'شبانة لتجارة الألوميتال',
        'دمياط الجديدة، طريق دمياط الجديدة – كفر البطيخ',
        'هاتف: 057-2401180', 'EGP', 'Africa/Cairo');

INSERT INTO sites (name_ar)
SELECT 'الفرع الرئيسي'
 WHERE NOT EXISTS (SELECT 1 FROM sites);

-- Placeholder admin (password set on first run). The trg_users_seed_permissions
-- trigger copies the 'admin' preset into user_permissions.
INSERT OR IGNORE INTO users (username, full_name, role, active, all_sites, password_hash, must_change_password)
VALUES ('admin', 'مدير النظام', 'admin', 1, 1, NULL, 1);

INSERT OR IGNORE INTO expense_categories (name_ar, sort) VALUES
  ('وجبات', 10), ('أجور', 20), ('إيجار', 30), ('كهرباء ومياه', 40),
  ('صيانة', 50), ('مواصلات', 60), ('مصروفات نثرية', 70), ('أخرى', 99);

-- Running order numbers start at 1 (provision_organization seeded these at 0).
INSERT OR IGNORE INTO doc_counters (scope, year, last_value) VALUES ('po_seq', 0, 0), ('so_seq', 0, 0);
