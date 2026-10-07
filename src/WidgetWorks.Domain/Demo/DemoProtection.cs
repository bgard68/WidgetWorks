namespace WidgetWorks.Domain.Demo;

/// <summary>
/// What the app says when a visitor tries to take an exhibit away.
///
/// The demo publishes its credentials, so everyone arrives as an administrator. Nearly everything they
/// can then do is fine: place orders, edit widgets, adjust inventory, fulfil what they placed. The
/// exceptions are the irreversible ones aimed at the seeded exhibits, and this is the refusal for
/// those.
///
/// The wording matters more than it looks. "Not allowed" reads as a broken demo; saying what the row
/// is and what to do instead turns a dead end into the next thing to try — and registering an account
/// is two clicks away.
/// </summary>
public static class DemoProtection
{
    public const string OrderMessage =
        "This is one of the demo's showcase orders, so it is kept as-is for the next visitor. " +
        "Place an order yourself and you can refund or fulfil that one.";
}
