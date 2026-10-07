-- Marks an order whose charge outcome the provider never gave us. Such an order keeps its stock
-- reservation and must NOT be failed on a timer: the money may already be gone, and releasing the
-- stock of a charge that actually landed is worse than holding it. Reconciliation clears this.
--
-- A column rather than a new status, deliberately. "Waiting for the payment to settle" is already
-- what AwaitingPayment means, and this is that with one extra fact attached; a parallel status would
-- have to be taught to the fulfilment state machine, the admin views and the SPA for no gain.
alter table orders add column if not exists payment_unconfirmed_at timestamptz;

-- The reconciliation sweep's only query. Partial, because the rows it wants are a tiny minority and
-- on a healthy day there are none at all.
create index if not exists ix_orders_payment_unconfirmed
    on orders (payment_unconfirmed_at)
    where payment_unconfirmed_at is not null;
