using WidgetWorks.Domain.Carts;

namespace WidgetWorks.Application.Carts;

/// <summary>
/// The single rule for who may touch a cart.
///
/// A guest cart carries no owner and is reachable by anyone holding its id — that is what lets a
/// visitor fill a basket before signing in, and it is a capability model: the id is the credential.
/// The moment a cart belongs to a user, only that user may reach it, so signing in genuinely
/// protects the basket instead of leaving it as exposed as a guest's.
///
/// Kept as one function rather than repeated per handler so a new cart operation cannot quietly ship
/// without the check, and so the rule can be tested on its own.
/// </summary>
public static class CartAccess
{
    /// <param name="cart">The cart that was loaded by id.</param>
    /// <param name="requestedBy">The signed-in user, or null for an anonymous caller.</param>
    public static bool IsPermitted(Cart cart, Guid? requestedBy)
    {
        ArgumentNullException.ThrowIfNull(cart);
        return cart.UserId is null || cart.UserId == requestedBy;
    }
}
