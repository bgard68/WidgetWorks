create table if not exists users (
    id                  uuid primary key,
    email               text not null,
    normalized_email    text not null,
    password_hash       text,
    role                text not null default 'Customer',
    security_stamp      uuid not null,
    is_protected_admin  boolean not null default false,
    two_factor_enabled  boolean not null default false,
    google_sub          text,
    failed_access_count integer not null default 0,
    locked_until        timestamptz,
    created_at          timestamptz not null
);

create unique index if not exists ux_users_normalized_email on users (normalized_email);
create unique index if not exists ux_users_google_sub on users (google_sub) where google_sub is not null;
