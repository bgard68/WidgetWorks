create table if not exists password_reset_tokens (
    id          uuid primary key,
    user_id     uuid not null references users(id) on delete cascade,
    token_hash  text not null,
    expires_at  timestamptz not null,
    used_at     timestamptz,
    created_at  timestamptz not null
);

create index if not exists ix_password_reset_tokens_hash on password_reset_tokens (token_hash);
create index if not exists ix_password_reset_tokens_user on password_reset_tokens (user_id);
