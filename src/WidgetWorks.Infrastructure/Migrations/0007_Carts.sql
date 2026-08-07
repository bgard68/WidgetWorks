create table if not exists carts (
    id          uuid primary key,
    user_id     uuid references users(id) on delete cascade,
    created_at  timestamptz not null,
    updated_at  timestamptz not null
);

-- At most one cart per registered user; guest carts (null user_id) are unconstrained.
create unique index if not exists ux_carts_user on carts (user_id) where user_id is not null;

create table if not exists cart_items (
    id          uuid primary key,
    cart_id     uuid not null references carts(id) on delete cascade,
    widget_id   uuid not null references widgets(id) on delete cascade,
    quantity    integer not null check (quantity > 0),
    added_at    timestamptz not null,
    constraint ux_cart_items_cart_widget unique (cart_id, widget_id)
);

create index if not exists ix_cart_items_cart on cart_items (cart_id);
