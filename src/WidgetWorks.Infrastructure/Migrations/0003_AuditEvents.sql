create table if not exists audit_events (
    id          uuid primary key,
    user_id     uuid,
    action      text not null,
    detail      text,
    created_at  timestamptz not null
);

create index if not exists ix_audit_events_user on audit_events (user_id);
create index if not exists ix_audit_events_action on audit_events (action);
create index if not exists ix_audit_events_created on audit_events (created_at);
