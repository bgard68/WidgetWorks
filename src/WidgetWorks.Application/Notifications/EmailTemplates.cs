using System.Globalization;
using System.Net;
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

    /// <summary>
    /// Invariant culture so a host whose locale uses a comma decimal separator cannot
    /// email "24,50" to a customer reading dollars.
    /// </summary>
    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Everything interpolated into the html body is data, and widget names plus tracking
    /// numbers are staff-supplied — encode them so a value containing markup cannot inject
    /// into a customer's inbox.
    /// </summary>
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static EmailMessage Build(Order order, string subject, string intro)
    {
        var htmlItems = string.Concat(order.Items.Select(i =>
            $"<li>{i.Quantity} &#215; {E(i.Name)} &#8212; ${Money(i.LineSubtotal)}</li>"));

        var html = EmailLayout.Document(
            $"<p>{E(intro)}</p>" +
            $"<p>Order <strong>{E(order.OrderNumber)}</strong></p>" +
            $"<ul style=\"padding-left:20px\">{htmlItems}</ul>" +
            "<p>" +
            $"Subtotal: ${Money(order.Subtotal)}<br>" +
            $"Shipping: ${Money(order.Shipping)}<br>" +
            $"Tax: ${Money(order.Tax)}<br>" +
            $"<strong>Total: ${Money(order.Total)}</strong>" +
            "</p>");

        var textItems = string.Join("\n", order.Items.Select(i => $"  {i.Quantity} x {i.Name} - ${Money(i.LineSubtotal)}"));
        var text =
            $"{intro}\n\nOrder {order.OrderNumber}\n{textItems}\n\n" +
            $"Subtotal: ${Money(order.Subtotal)}\nShipping: ${Money(order.Shipping)}\n" +
            $"Tax: ${Money(order.Tax)}\nTotal: ${Money(order.Total)}";

        return new EmailMessage(order.Email, subject, html, text);
    }
}
