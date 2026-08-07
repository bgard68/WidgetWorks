using WidgetWorks.Domain.Catalog;
using Xunit;

namespace WidgetWorks.UnitTests;

public class WidgetDomainTests
{
    [Fact]
    public void QuantityAvailable_is_on_hand_minus_reserved()
    {
        var widget = new Widget { QuantityOnHand = 10, QuantityReserved = 3 };
        Assert.Equal(7, widget.QuantityAvailable);
        Assert.True(widget.IsInStock);
    }

    [Fact]
    public void QuantityAvailable_never_goes_negative()
    {
        var widget = new Widget { QuantityOnHand = 2, QuantityReserved = 5 };
        Assert.Equal(0, widget.QuantityAvailable);
        Assert.False(widget.IsInStock);
    }
}
