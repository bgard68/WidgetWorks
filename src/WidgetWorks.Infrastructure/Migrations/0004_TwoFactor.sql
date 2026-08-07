create table if not exists two_factor_secrets (
    user_id      uuid primary key references users (id) on delete cascade,
    secret       text not null,
    is_confirmed boolean not null default false,
    created_at   timestamptz not null
);

create table if not exists recovery_codes (
    id         uuid primary key,
    user_id    uuid not null references users (id) on delete cascade,
    code_hash  text not null,
    used_at    timestamptz,
    created_at timestamptz not null
);

create index if not exists ix_recovery_codes_user on recovery_codes (user_id);
