using WidgetWorks.Application.Carts;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Pricing;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The fulfilment state machine, now owned by the order itself, and the pricer both quoting and
/// checkout share. The pricer is the interesting one: it exists so the total a shopper is shown and
/// the total they are charged cannot drift apart.
/// </summary>
public class OrderStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static Order OrderIn(string status) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
    };

    // ---- allowed transitions -------------------------------------------------------------

    [Theory]
    [InlineData(OrderStatus.Paid, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Paid, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Delivered)]
    public void A_legal_transition_is_allowed(string from, string to)
    {
        Assert.True(OrderStatus.CanTransition(from, to));
        Assert.True(OrderIn(from).CanTransitionTo(to));
    }

    [Theory]
    // Nothing ships before it is paid for -- including an order still settling.
    [InlineData(OrderStatus.Pending, OrderStatus.Shipped)]
    [InlineData(OrderStatus.AwaitingPayment, OrderStatus.Shipped)]
    [InlineData(OrderStatus.AwaitingPayment, OrderStatus.Delivered)]
    [InlineData(OrderStatus.PaymentFailed, OrderStatus.Shipped)]
    // No skipping a step, and no going backwards.
    [InlineData(OrderStatus.Paid, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Paid)]
    // Terminal states are terminal.
    [InlineData(OrderStatus.Delivered, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Shipped)]
    // A status is not a transition to itself.
    [InlineData(OrderStatus.Paid, OrderStatus.Paid)]
    public void An_illegal_transition_is_refused(string from, string to)
    {
        Assert.False(OrderStatus.CanTransition(from, to));
        Assert.False(OrderIn(from).CanTransitionTo(to));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Shipped ")]
    [InlineData("shipped")]
    [InlineData("Nonsense")]
    public void An_unrecognized_target_is_refused_rather_than_matched_loosely(string? target)
    {
        Assert.False(OrderIn(OrderStatus.Paid).CanTransitionTo(target));
    }

    [Fact]
    public void Allowed_next_reports_the_options_for_a_status()
    {
        Assert.Equal([OrderStatus.Shipped, OrderStatus.Cancelled], OrderStatus.AllowedNext(OrderStatus.Paid));
        Assert.Equal([OrderStatus.Delivered], OrderStatus.AllowedNext(OrderStatus.Shipped));
        Assert.Empty(OrderStatus.AllowedNext(OrderStatus.Delivered));
        Assert.Empty(OrderStatus.AllowedNext("not-a-status"));
        Assert.Empty(OrderStatus.AllowedNext(null));
    }

    // ---- applying a transition -----------------------------------------------------------

    [Fact]
    public void Transitioning_sets_the_status_tracking_and_timestamp()
    {
        var order = OrderIn(OrderStatus.Paid);

        order.TransitionTo(OrderStatus.Shipped, "  1Z999AA10123456784  ", Now);

        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal("1Z999AA10123456784", order.TrackingNumber);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Transitioning_without_a_tracking_number_keeps_the_existing_one(string? tracking)
    {
        var order = OrderIn(OrderStatus.Shipped);
        order.TrackingNumber = "1Z-ORIGINAL";

        order.TransitionTo(OrderStatus.Delivered, tracking, Now);

        // Marking delivered must not wipe the number the customer is tracking with.
        Assert.Equal("1Z-ORIGINAL", order.TrackingNumber);
    }

    [Fact]
    public void Transitioning_illegally_throws_and_changes_nothing()
    {
        var order = OrderIn(OrderStatus.AwaitingPayment);

        var ex = Assert.Throws<InvalidOperationException>(
            () => order.TransitionTo(OrderStatus.Shipped, "1Z-NEW", Now));

        Assert.Contains("AwaitingPayment", ex.Message);
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Null(order.TrackingNumber);
        Assert.Equal(Now.AddDays(-1), order.UpdatedAt);
    }

    [Fact]
    public void Unit_count_sums_quantities_rather_than_counting_lines()
    {
        var order = OrderIn(OrderStatus.Paid);
        order.Items =
        [
            new OrderItem { Quantity = 2 },
            new OrderItem { Quantity = 3 },
        ];

        Assert.Equal(5, order.UnitCount);
        Assert.Equal(0, OrderIn(OrderStatus.Paid).UnitCount);
    }

    // ---- the shared pricer ---------------------------------------------------------------

    private static readonly OrderPricer Pricer = new(
        new FlatRateShippingCalculator(),
        new StateSalesTaxCalculator(new StaticStateTaxRateProvider()));

    private static CartView Cart(decimal subtotal, int itemCount) =>
        new(Guid.NewGuid(), null, [], subtotal, itemCount);

    [Fact]
    public void Pricing_adds_shipping_and_tax_to_the_subtotal()
    {
        var priced = Pricer.Price(Cart(20m, 2), "CA", "Standard");

        Assert.Equal(20m, priced.Subtotal);
        Assert.Equal(7.74m, priced.Shipping);     // 6.99 + one extra item at 0.75
        Assert.Equal(0.0725m, priced.TaxRate);
        Assert.Equal(1.45m, priced.Tax);          // 20 * 0.0725
        Assert.Equal(29.19m, priced.Total);
        Assert.False(priced.IsEmpty);
    }

    [Fact]
    public void Tax_is_charged_on_the_subtotal_only_never_on_shipping()
    {
        var standard = Pricer.Price(Cart(20m, 1), "CA", "Standard");
        var express = Pricer.Price(Cart(20m, 1), "CA", "Express");

        Assert.NotEqual(standard.Shipping, express.Shipping);
        Assert.Equal(standard.Tax, express.Tax);
    }

    [Theory]
    [InlineData("OR")]
    [InlineData("AK")]
    [InlineData("DE")]
    [InlineData("MT")]
    [InlineData("NH")]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData(null)]
    public void A_state_with_no_rate_costs_no_tax(string? state)
    {
        var priced = Pricer.Price(Cart(100m, 1), state, "Standard");

        Assert.Equal(0m, priced.Tax);
        Assert.Equal(0m, priced.TaxRate);
        Assert.Equal(100m, priced.Total);        // free shipping over 75, no tax
    }

    [Fact]
    public void An_empty_cart_is_not_charged_for_delivery()
    {
        var priced = Pricer.Price(Cart(0m, 0), "CA", "Express");

        Assert.True(priced.IsEmpty);
        Assert.Equal(0m, priced.Shipping);
        Assert.Equal(0m, priced.Total);
    }

    [Fact]
    public void The_quote_and_the_charge_are_the_same_calculation()
    {
        // Same inputs through the one component both paths use: they cannot drift.
        var cart = Cart(89.97m, 3);

        var shown = Pricer.Price(cart, "CA", "Standard");
        var charged = Pricer.Price(cart, "CA", "Standard");

        Assert.Equal(shown, charged);
        Assert.Equal(0m, shown.Shipping);         // over the free threshold
        Assert.Equal(6.52m, shown.Tax);           // round(89.97 * 0.0725)
        Assert.Equal(96.49m, shown.Total);
    }

    // ---- order drafting ------------------------------------------------------------------

    [Fact]
    public void An_order_number_is_dated_and_short_enough_to_read_out()
    {
        var id = Guid.Parse("a1b2c3d4-e5f6-0000-0000-000000000000");

        var number = OrderDraft.NumberFor(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), id);

        Assert.Equal("WW-20260501-A1B2C3D4E5", number);
    }

    [Fact]
    public void An_order_number_carries_enough_of_the_id_to_make_a_collision_negligible()
    {
        var number = OrderDraft.NumberFor(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());

        // order_number is uniquely indexed, so a collision is a failed checkout rather than a leak,
        // and collisions arrive by the birthday bound. Ten hex characters is 40 bits; six was 24,
        // which is a coin flip at about five thousand orders in one day. This pins the width so it
        // cannot be shortened back for tidiness.
        var suffix = number.Split('-')[2];
        Assert.Equal(10, suffix.Length);
        Assert.Equal(suffix.ToUpperInvariant(), suffix);
    }

    [Fact]
    public void A_drafted_order_snapshots_the_price_and_normalizes_the_address()
    {
        var cart = new CartView(Guid.NewGuid(), null,
            [new CartLineView(Guid.NewGuid(), "WW-1", "Standard Widget", 10m, 2, 5, 20m)], 20m, 2);
        var priced = Pricer.Price(cart, "ca", "Standard");
        var address = new ShippingAddressInput("  Jane Doe  ", " 1 Main St ", "   ", " Springfield ", " ca ", " 90210 ", null);

        var order = OrderDraft.Create(cart, priced, address, "jane@example.com", null, Now, Guid.NewGuid(), Guid.NewGuid);

        Assert.Equal("Jane Doe", order.ShipName);
        Assert.Equal("1 Main St", order.ShipLine1);
        Assert.Null(order.ShipLine2);                 // whitespace-only becomes absent, not blank
        Assert.Equal("CA", order.ShipState);
        Assert.Equal("US", order.ShipCountry);        // defaulted
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(priced.Tax, order.Tax);
        Assert.Equal(priced.TaxRate, order.TaxRate);
        Assert.Equal(priced.Total, order.Total);
        Assert.Equal(2, order.UnitCount);
        Assert.Equal(Now, order.CreatedAt);
    }
}
