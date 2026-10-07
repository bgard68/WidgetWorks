-- Retry safety for POST /checkout. The row is claimed BEFORE the order is placed, so there is no
-- window in which an order exists without a key recording it; the reverse ordering is the classic
-- hole where a crash after the insert leaves a paid order no retry can ever recognise.
create table if not exists idempotency_keys (
    scope           text not null,
    key             text not null,
    request_hash    text not null,
    status          text not null,
    response_body   text,
    response_error  text,
    created_at      timestamptz not null,
    completed_at    timestamptz,

    -- The primary key IS the concurrency control: two simultaneous claims both insert, and
    -- Postgres rejects the second. No advisory lock, no read-then-write race.
    primary key (scope, key)
);

-- Retention sweeps delete by age, so the scan has to be cheap.
create index if not exists ix_idempotency_keys_created_at on idempotency_keys (created_at);
