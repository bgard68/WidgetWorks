-- Data-layer enforcement (defense-in-depth) of the immutable seeded administrator.
-- The protected admin row cannot be deleted, and its identity (email, role, password,
-- protected flag) cannot change -- so the showcase always has a working super-admin.
-- Security operations that do NOT change identity (security_stamp rotation, lockout
-- counters, 2FA toggles, google_sub linking) remain allowed.

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
    end if;

    return new;
end;
$$ language plpgsql;

drop trigger if exists trg_guard_protected_admin on users;
create trigger trg_guard_protected_admin
    before update or delete on users
    for each row execute function guard_protected_admin();
