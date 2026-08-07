create table if not exists orders (
    id                uuid primary key,
    order_number      text not null,
    user_id           uuid references users(id) on delete set null,
    email             text not null,
    ship_name         text not null,
    ship_line1        text not null,
    ship_line2        text,
    ship_city         text not null,
    ship_state        text not null,
    ship_postal_code  text not null,
    ship_country      text not null default 'US',
    subtotal          numeric(12,2) not null,
    shipping_method   text not null,
    shipping          numeric(12,2) not null,
    tax_state         text not null,
    tax_rate          numeric(6,5) not null,
    tax               numeric(12,2) not null,
    total             numeric(12,2) not null,
    status            text not null,
    payment_provider  text,
    payment_reference text,
    created_at        timestamptz not null,
    updated_at        timestamptz not null
);

create unique index if not exists ux_orders_number on orders (order_number);
create index if not exists ix_orders_user on orders (user_id);
create index if not exists ix_orders_email on orders (lower(email));

create table if not exists order_items (
    id            uuid primary key,
    order_id      uuid not null references orders(id) on delete cascade,
    widget_id     uuid not null references widgets(id),
    sku           text not null,
    name          text not null,
    unit_price    numeric(12,2) not null,
    quantity      integer not null check (quantity > 0),
    line_subtotal numeric(12,2) not null
);

create index if not exists ix_order_items_order on order_items (order_id);
