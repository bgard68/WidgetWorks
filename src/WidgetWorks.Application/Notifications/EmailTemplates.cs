using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Orders;

namespace WidgetWorks.Application.Notifications;

/// <summary>Builds the HTML + plain-text transactional emails for order events.</summary>
public static class EmailTemplates
{
    public static EmailMessage OrderReceived(Order order) => Build(
        order,
        $"WidgetWorks order {order.OrderNumber} received",
        "Thanks for your order! We've received it and it's now being processed.");

    public static EmailMessage OrderShipped(Order order) => Build(
        order,
        $"Your WidgetWorks order {order.OrderNumber} has shipped",
        "Good news — your order has shipped."
            + (string.IsNullOrWhiteSpace(order.TrackingNumber) ? string.Empty : $" Tracking number: {order.TrackingNumber}."));

    public static EmailMessage OrderCancelled(Order order) => Build(
        order,
        $"Your WidgetWorks order {order.OrderNumber} was cancelled",
        "Your order has been cancelled. If this is unexpected, please contact support.");

    private static EmailMessage Build(Order order, string subject, string intro)
    {
        var htmlItems = string.Concat(order.Items.Select(i =>
            $"<li>{i.Quantity} × {i.Name} — ${i.LineSubtotal:0.00}</li>"));
        var html =
            $"<p>{intro}</p>" +
            $"<p>Order <strong>{order.OrderNumber}</strong></p>" +
            $"<ul>{htmlItems}</ul>" +
            $"<p>Subtotal: ${order.Subtotal:0.00}<br/>Shipping: ${order.Shipping:0.00}<br/>Tax: ${order.Tax:0.00}<br/>" +
            $"<strong>Total: ${order.Total:0.00}</strong></p>";

        var textItems = string.Join("\n", order.Items.Select(i => $"  {i.Quantity} x {i.Name} - ${i.LineSubtotal:0.00}"));
        var text =
            $"{intro}\n\nOrder {order.OrderNumber}\n{textItems}\n\n" +
            $"Subtotal: ${order.Subtotal:0.00}\nShipping: ${order.Shipping:0.00}\nTax: ${order.Tax:0.00}\nTotal: ${order.Total:0.00}";

        return new EmailMessage(order.Email, subject, html, text);
    }
}
