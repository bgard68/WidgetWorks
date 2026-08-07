create table if not exists widgets (
    id                 uuid primary key,
    sku                text not null,
    name               text not null,
    description        text not null default '',
    image_url          text,
    price              numeric(12,2) not null default 0,
    is_active          boolean not null default true,
    quantity_on_hand   integer not null default 0,
    quantity_reserved  integer not null default 0,
    created_at         timestamptz not null,
    updated_at         timestamptz not null,
    constraint ck_widgets_price_nonneg check (price >= 0),
    constraint ck_widgets_onhand_nonneg check (quantity_on_hand >= 0),
    constraint ck_widgets_reserved_range check (quantity_reserved >= 0 and quantity_reserved <= quantity_on_hand)
);

create unique index if not exists ux_widgets_sku on widgets (upper(sku));
create index if not exists ix_widgets_active_name on widgets (is_active, name);
