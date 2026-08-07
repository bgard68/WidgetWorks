create table if not exists refresh_tokens (
    id           uuid primary key,
    user_id      uuid not null references users (id) on delete cascade,
    token_hash   text not null,
    family_id    uuid not null,
    replaced_by  uuid,
    expires_at   timestamptz not null,
    revoked_at   timestamptz,
    created_at   timestamptz not null
);

create unique index if not exists ux_refresh_tokens_hash on refresh_tokens (token_hash);
create index if not exists ix_refresh_tokens_user on refresh_tokens (user_id);
create index if not exists ix_refresh_tokens_family on refresh_tokens (family_id);
