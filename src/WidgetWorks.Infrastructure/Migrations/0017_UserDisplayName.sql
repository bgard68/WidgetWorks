-- An optional name the shopper chooses, so the header can greet them and an order can carry a human
-- name rather than an email address.
--
-- Nullable, and registration deliberately does not ask for it. Making signup collect a name would mean
-- changing the register flow, the seeder and the order snapshot for something purely cosmetic; letting
-- people set it later costs nothing and leaves the signup path untouched.
alter table users add column if not exists display_name text;

-- The greeting falls back to nothing rather than to an empty string, so "Hello," with a trailing comma
-- is impossible. A name of only spaces is the same as no name.
alter table users drop constraint if exists ck_users_display_name_not_blank;
alter table users add constraint ck_users_display_name_not_blank
    check (display_name is null or length(btrim(display_name)) > 0);
