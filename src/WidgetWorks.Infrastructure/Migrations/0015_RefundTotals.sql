-- How much of an order has been given back. Cumulative, so several partial refunds cannot add up to
-- more than was charged, and the status only becomes Refunded once the figure reaches the total: a
-- part-refunded order is still a paid order with goods owed.
--
-- NOT NULL with a default rather than nullable: "no refund" is zero, not unknown, and every existing
-- row genuinely has had nothing refunded.
alter table orders add column if not exists refunded_total numeric(12,2) not null default 0;

-- The guard that stops two staff refunding at once from both applying their amount relies on
-- comparing against the stored figure, so it must never be negative or exceed the order.
alter table orders drop constraint if exists ck_orders_refunded_range;
alter table orders add constraint ck_orders_refunded_range
    check (refunded_total >= 0 and refunded_total <= total);
