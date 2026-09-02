-- Realign the demo catalog rows the seeder can no longer reach.
--
-- DbSeeder inserts a demo widget only when its SKU is absent and never updates one, so a
-- database keeps whatever name, description and price each SKU carried on the day it was
-- first inserted. That is the right rule for a seed -- a restart must not overwrite an
-- administrator's catalogue edit -- but it means renaming a seeded product in code does
-- not reach a database that already has it.
--
-- Two rounds of renaming were stranded that way. WW-001..WW-005 still held the five
-- original products from the first seed, and those five SKUs were later reassigned to
-- different products entirely, so their name, description and price all described the
-- wrong item -- WW-003 offered a $12.99 Standard Widget Valve at $49.99, and WW-005 shared
-- the name "Widget Pro Kit" with WW-021. WW-006..WW-025 were one round behind: correctly
-- priced, but still missing the finish that now distinguishes three variants of each shape.
--
-- Correcting seeded content is a migration's job rather than the seeder's, because it has
-- to happen exactly once. Quantities are deliberately untouched: stock is operational, it
-- moves with orders and reservations, and lowering quantity_on_hand here could contradict
-- quantity_reserved and fail ck_widgets_reserved_range.
with corrected (sku, name, description, price) as (
    values
        ('WW-001', 'Standard Widget Block Cobalt', 'The dependable everyday widget.', 9.99),
        ('WW-002', 'Standard Widget Rotary Cobalt', 'Dial-adjustable everyday widget.', 11.49),
        ('WW-003', 'Standard Widget Valve Cobalt', 'Inline everyday widget with twin ports.', 12.99),
        ('WW-004', 'Standard Widget Hub Cobalt', 'Four-port everyday widget.', 13.99),
        ('WW-005', 'Standard Widget Turbine Cobalt', 'Ventilated everyday widget.', 14.99),
        ('WW-006', 'Deluxe Widget Block Fuchsia', 'Premium finish with a reinforced housing.', 24.99),
        ('WW-007', 'Deluxe Widget Rotary Fuchsia', 'Premium dial with a gold-plated pointer.', 27.49),
        ('WW-008', 'Deluxe Widget Valve Fuchsia', 'Premium inline widget, machined ports.', 29.99),
        ('WW-009', 'Deluxe Widget Hub Fuchsia', 'Premium four-port widget, gold contacts.', 32.99),
        ('WW-010', 'Deluxe Widget Turbine Fuchsia', 'Premium ventilated widget, balanced rotor.', 34.99),
        ('WW-011', 'Mega Widget Block Copper', 'Oversized widget for heavy-duty jobs.', 49.99),
        ('WW-012', 'Mega Widget Rotary Copper', 'Oversized dial widget for heavy-duty jobs.', 54.49),
        ('WW-013', 'Mega Widget Valve Copper', 'Oversized inline widget, high-flow ports.', 59.99),
        ('WW-014', 'Mega Widget Hub Copper', 'Oversized four-port widget for busy lines.', 64.99),
        ('WW-015', 'Mega Widget Turbine Copper', 'Oversized ventilated widget, high throughput.', 69.99),
        ('WW-016', 'Mini Widget Block Jade', 'Compact widget for tight spaces.', 4.99),
        ('WW-017', 'Mini Widget Rotary Jade', 'Compact dial widget for tight spaces.', 5.99),
        ('WW-018', 'Mini Widget Valve Jade', 'Compact inline widget, low-flow ports.', 6.49),
        ('WW-019', 'Mini Widget Hub Jade', 'Compact four-port widget for tight spaces.', 7.49),
        ('WW-020', 'Mini Widget Turbine Jade', 'Compact ventilated widget, quiet running.', 8.49),
        ('WW-021', 'Widget Pro Kit Plum', 'Bundle of assorted widgets and accessories.', 79.99),
        ('WW-022', 'Widget Starter Kit Plum', 'Open-tray bundle for a first build.', 39.99),
        ('WW-023', 'Widget Builder Kit Plum', 'Three-drawer cabinet of sorted widgets.', 99.99),
        ('WW-024', 'Widget Travel Kit Plum', 'Soft-sided bundle for work away from the bench.', 49.99),
        ('WW-025', 'Widget Master Kit Plum', 'Two-case bundle covering the full range.', 149.99)
)
update widgets w
   set name        = c.name,
       description = c.description,
       price       = c.price::numeric(12,2),
       updated_at  = now()
  from corrected c
 where upper(w.sku) = upper(c.sku)
   and (w.name        is distinct from c.name
     or w.description is distinct from c.description
     or w.price       is distinct from c.price::numeric(12,2));
