-- 0013 — add a 'credit' tender so a client's stored credit can settle a future
-- order as a non-cash payment (never touches a drawer). Must be its own
-- migration: Postgres forbids using a freshly-added enum value in the same
-- transaction it is added in.
alter type payment_method add value if not exists 'credit';
