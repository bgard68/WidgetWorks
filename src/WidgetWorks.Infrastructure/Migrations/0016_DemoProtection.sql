-- Keeps the public demo usable after a visitor has had a go at it.
--
-- The demo publishes its credentials, so every visitor arrives with Administrator rights. Most of
-- what they can then do is harmless or heals on the next restart: the seeder re-inserts missing
-- accounts and re-seeds the catalogue. Two things do not heal, and this is for those.
--
-- A marked order is refused only by refund and cancel, not by everything: a visitor can still place
-- orders, ship and deliver these ones, edit widgets and adjust inventory. What they cannot do is make
-- an exhibit disappear, since nothing re-seeds orders.
alter table orders add column if not exists is_protected boolean not null default false;

-- Partial, because the protected rows are a small fixed set and the query that cares is the
-- destructive one asking "is this one of them?".
create index if not exists ix_orders_protected on orders (id) where is_protected;

-- Extends the existing protected-account guard (0006) to cover 2FA.
--
-- Enrolling 2FA on the shared demo administrator is the one action a visitor can take that nothing
-- recovers from: signing in then needs a code only they have, the recovery codes were shown only to
-- them, and turning it off needs a session nobody can get. The seeder cannot help either — it is
-- insert-if-absent, so it leaves an existing account alone.
--
-- Stamp rotation, lockout counters and google_sub linking stay allowed, as before: they log sessions
-- out at worst and cost nothing permanent.
create or replace function guard_protected_admin() returns trigger as $$
begin
    if (tg_op = 'DELETE') then
        if (old.is_protected_admin) then
            raise exception 'The protected administrator account cannot be deleted.';
        end if;
        return old;
    end if;

    -- UPDATE
    if (old.is_protected_admin) then
        if (new.is_protected_admin is distinct from old.is_protected_admin)
           or (new.normalized_email is distinct from old.normalized_email)
           or (new.email is distinct from old.email)
           or (new.role is distinct from old.role)
           or (new.password_hash is distinct from old.password_hash) then
            raise exception 'The protected administrator''s identity cannot be changed.';
        end if;

        if (new.two_factor_enabled and not old.two_factor_enabled) then
            raise exception 'Two-factor authentication cannot be enabled on a protected demo account.';
        end if;
    end if;

    return new;
end;
$$ language plpgsql;
