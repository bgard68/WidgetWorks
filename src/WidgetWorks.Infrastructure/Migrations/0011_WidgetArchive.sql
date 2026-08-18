-- Retiring a widget must not destroy order history.
--
-- order_items.widget_id references widgets(id) with no delete rule, so a widget
-- that has ever been ordered cannot be removed without breaking those rows. A
-- widget with sales is therefore archived instead of deleted: the row stays so
-- order joins and reporting still resolve it, while archived_at takes it out of
-- the storefront and the admin working set from that point on. Widgets that have
-- never been ordered carry no history and are deleted outright.
alter table widgets add column if not exists archived_at timestamptz;

-- Both the storefront and the admin list read the live set ordered by name.
create index if not exists ix_widgets_live_name on widgets (name) where archived_at is null;
