using WidgetWorks.Domain.Audit;
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

    [Fact]
    public void An_audit_event_carries_who_did_what_and_when()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var evt = new AuditEvent { Id = id, UserId = userId, Action = "login.success", Detail = "ip 10.0.0.1", CreatedAt = at };

        Assert.Equal(id, evt.Id);
        Assert.Equal(userId, evt.UserId);
        Assert.Equal("login.success", evt.Action);
        Assert.Equal("ip 10.0.0.1", evt.Detail);
        Assert.Equal(at, evt.CreatedAt);
    }
}
