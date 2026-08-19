using System.Net.Mime;
using System.Text;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Notifications;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Email;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// What actually lands in someone's inbox. Two real bugs live here in history: the HTML part
/// rendering blank because the message was assembled as two alternate views with no body, and a
/// widget name containing an ampersand corrupting the layout because values were interpolated into
/// HTML unescaped. Both are now assertions rather than memories.
/// </summary>
public class EmailMessageTests
{
    private static readonly EmailOptions Options = new()
    {
        FromAddress = "no-reply@widgetworks.demo",
        FromName = "WidgetWorks",
        Host = "localhost",
        Port = 1025,
    };

    private static Order OrderWith(string widgetName, decimal price = 1234.56m) => new()
    {
        Id = Guid.NewGuid(),
        OrderNumber = "WW-20260501-ABC123",
        Email = "jane@example.com",
        ShipName = "Jane Doe",
        ShipLine1 = "1 Main St",
        ShipCity = "Springfield",
        ShipState = "CA",
        ShipPostalCode = "90210",
        ShipCountry = "US",
        Subtotal = price,
        ShippingMethod = "Standard",
        Shipping = 6.99m,
        TaxState = "CA",
        TaxRate = 0.0725m,
        Tax = 89.51m,
        Total = price + 6.99m + 89.51m,
        Status = OrderStatus.Paid,
        CreatedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero),
        Items =
        [
            new OrderItem
            {
                Id = Guid.NewGuid(),
                WidgetId = Guid.NewGuid(),
                Sku = "WW-001",
                Name = widgetName,
                UnitPrice = price,
                Quantity = 1,
                LineSubtotal = price,
            },
        ],
    };

    // ---- MIME shape ----------------------------------------------------------------------

    [Fact]
    public void The_body_is_plain_text_with_html_as_the_alternative()
    {
        var message = new EmailMessage("jane@example.com", "Subject", "<p>Hi</p>", "Hi");

        using var mail = SmtpEmailSender.BuildMailMessage(Options, message);

        // The shape that made the HTML part render as blank was: empty Body + two alternate views.
        Assert.Equal("Hi", mail.Body);
        Assert.False(mail.IsBodyHtml);
        var html = Assert.Single(mail.AlternateViews);
        Assert.Equal(MediaTypeNames.Text.Html, html.ContentType.MediaType);
    }

    [Fact]
    public void Everything_is_utf8_so_accents_and_currency_survive()
    {
        var message = new EmailMessage("jörg@example.com", "Your order — £12.50 réservé", "<p>£</p>", "£12.50 réservé");

        using var mail = SmtpEmailSender.BuildMailMessage(Options, message);

        Assert.Equal(Encoding.UTF8, mail.SubjectEncoding);
        Assert.Equal(Encoding.UTF8, mail.BodyEncoding);
        Assert.Equal(Encoding.UTF8, mail.AlternateViews[0].ContentType.CharSet is null
            ? Encoding.UTF8
            : Encoding.GetEncoding(mail.AlternateViews[0].ContentType.CharSet!));
    }

    [Fact]
    public void The_sender_and_recipient_come_from_configuration_and_the_message()
    {
        var message = new EmailMessage("jane@example.com", "Subject", "<p>Hi</p>", "Hi");

        using var mail = SmtpEmailSender.BuildMailMessage(Options, message);

        Assert.Equal("no-reply@widgetworks.demo", mail.From!.Address);
        Assert.Equal("WidgetWorks", mail.From.DisplayName);
        Assert.Equal("jane@example.com", Assert.Single(mail.To).Address);
    }

    // ---- template escaping ---------------------------------------------------------------

    [Fact]
    public void A_widget_name_with_markup_characters_is_escaped_in_the_html()
    {
        var order = OrderWith("Widget & Co <Pro> \"Special\"");

        var email = EmailTemplates.OrderReceived(order);

        // Escaped in HTML...
        Assert.Contains("&amp;", email.HtmlBody);
        Assert.Contains("&lt;Pro&gt;", email.HtmlBody);
        Assert.DoesNotContain("<Pro>", email.HtmlBody);

        // ...and left alone in the text part, where it is not markup.
        Assert.Contains("Widget & Co <Pro>", email.TextBody);
    }

    [Fact]
    public void The_html_is_a_document_with_a_charset_so_clients_do_not_guess()
    {
        var email = EmailTemplates.OrderReceived(OrderWith("Standard Widget"));

        Assert.Contains("<!DOCTYPE html", email.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("charset", email.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Money_is_formatted_invariantly_so_a_server_locale_cannot_change_a_total()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A German locale renders a decimal comma; a receipt that disagrees with the charge is
            // a support ticket at best. The template formats "0.00" under InvariantCulture, so the
            // separator is a dot and there are no thousands groupings at all.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var email = EmailTemplates.OrderReceived(OrderWith("Standard Widget", 1234.56m));

            Assert.Contains("1234.56", email.TextBody);
            Assert.DoesNotContain("1234,56", email.TextBody);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_receipt_carries_the_order_number_the_lines_and_the_total()
    {
        var order = OrderWith("Standard Widget");

        var email = EmailTemplates.OrderReceived(order);

        Assert.Equal(order.Email, email.To);
        Assert.Contains(order.OrderNumber, email.Subject + email.TextBody);
        Assert.Contains("Standard Widget", email.TextBody);
        Assert.Contains("1331.06", email.TextBody);   // 1234.56 + 6.99 + 89.51
    }

    [Fact]
    public void Shipped_and_cancelled_notices_say_which_order_they_are_about()
    {
        var order = OrderWith("Standard Widget");
        order.TrackingNumber = "1Z999AA10123456784";

        var shipped = EmailTemplates.OrderShipped(order);
        var cancelled = EmailTemplates.OrderCancelled(order);

        Assert.Contains(order.OrderNumber, shipped.Subject + shipped.TextBody);
        Assert.Contains("1Z999AA10123456784", shipped.TextBody);
        Assert.Contains(order.OrderNumber, cancelled.Subject + cancelled.TextBody);
        Assert.All(new[] { shipped, cancelled }, e => Assert.Equal(order.Email, e.To));
    }

    [Fact]
    public void Account_emails_are_addressed_and_carry_their_link()
    {
        var welcome = AccountEmailTemplates.Welcome("jane@example.com");
        var reset = AccountEmailTemplates.PasswordReset("jane@example.com", "https://app.test/reset?token=abc");

        Assert.Equal("jane@example.com", welcome.To);
        Assert.NotEmpty(welcome.HtmlBody);
        Assert.Equal("jane@example.com", reset.To);
        Assert.Contains("https://app.test/reset?token=abc", reset.TextBody);
        Assert.Contains("https://app.test/reset?token=abc", reset.HtmlBody);
    }

    [Fact]
    public void Every_template_supplies_both_a_text_and_an_html_part()
    {
        var order = OrderWith("Standard Widget");

        EmailMessage[] all =
        [
            EmailTemplates.OrderReceived(order),
            EmailTemplates.OrderShipped(order),
            EmailTemplates.OrderCancelled(order),
            AccountEmailTemplates.Welcome("jane@example.com"),
            AccountEmailTemplates.PasswordReset("jane@example.com", "https://app.test/r"),
        ];

        Assert.All(all, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Subject));
            Assert.False(string.IsNullOrWhiteSpace(e.TextBody));
            Assert.False(string.IsNullOrWhiteSpace(e.HtmlBody));
        });
    }
}
