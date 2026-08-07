using WidgetWorks.Application.Checkout.Quote;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class PricingTests
{
    private readonly StateSalesTaxCalculator _tax = new();
    private readonly FlatRateShippingCalculator _shipping = new();

    [Fact]
    public void Tax_uses_state_rate()
    {
        var line = _tax.Calculate("CA", 100m);
        Assert.Equal(0.0725m, line.Rate);
        Assert.Equal(7.25m, line.Amount);
    }

    [Fact]
    public void Tax_is_zero_for_no_tax_state()
    {
        Assert.Equal(0m, _tax.Calculate("OR", 100m).Amount);
    }

    [Fact]
    public void Tax_is_zero_for_unknown_state()
    {
        Assert.Equal(0m, _tax.Calculate("ZZ", 100m).Amount);
        Assert.Equal(0m, _tax.Calculate(null, 100m).Amount);
    }

    [Fact]
    public void Standard_shipping_is_free_over_threshold()
    {
        Assert.Equal(0m, _shipping.Calculate("Standard", 80m, 1).Amount);
    }

    [Fact]
    public void Standard_shipping_charges_base_plus_per_item_under_threshold()
    {
        var quote = _shipping.Calculate("standard", 20m, 3);
        Assert.Equal("Standard", quote.Method);
        Assert.Equal(6.99m + (2 * 0.75m), quote.Amount);
    }

    [Fact]
    public void Express_shipping_is_not_free()
    {
        var quote = _shipping.Calculate("Express", 500m, 1);
        Assert.Equal("Express", quote.Method);
        Assert.Equal(19.99m, quote.Amount);
    }

    [Fact]
    public async Task Quote_assembles_subtotal_shipping_tax_total()
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = 100 };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, default, CancellationToken.None);

        var handler = new QuoteCartHandler(carts, widgets, _shipping, _tax);
        var result = await handler.Handle(new QuoteCartCommand(cart.Id, "CA", "Standard"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var q = result.Value!;
        Assert.Equal(20m, q.Subtotal);            // 2 * 10
        Assert.Equal(6.99m, q.Shipping);          // under free threshold, single line
        Assert.Equal(1.45m, q.Tax);               // 20 * 0.0725
        Assert.Equal(28.44m, q.Total);            // 20 + 6.99 + 1.45
    }

    [Fact]
    public async Task Quote_for_empty_cart_is_all_zero()
    {
        var widgets = new InMemoryWidgetRepository();
        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);

        var handler = new QuoteCartHandler(carts, widgets, _shipping, _tax);
        var result = await handler.Handle(new QuoteCartCommand(cart.Id, "CA", "Standard"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsEmpty);
        Assert.Equal(0m, result.Value!.Total);
    }
}
