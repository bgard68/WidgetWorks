using WidgetWorks.Domain.Users;
using Xunit;

namespace WidgetWorks.UnitTests;

public class ProtectedAdminGuardTests
{
    private static User ProtectedAdmin() => new()
    {
        Id = Guid.NewGuid(),
        Email = "admin@widgetworks.demo",
        Role = UserRoles.Administrator,
        IsProtectedAdmin = true,
    };

    [Fact]
    public void Delete_of_protected_admin_is_blocked()
    {
        Assert.Throws<ProtectedAdminException>(() => ProtectedAdminGuard.EnsureCanDelete(ProtectedAdmin()));
    }

    [Fact]
    public void Delete_of_ordinary_user_is_allowed()
    {
        var user = new User { Id = Guid.NewGuid(), Role = UserRoles.Customer, IsProtectedAdmin = false };
        ProtectedAdminGuard.EnsureCanDelete(user); // does not throw
    }

    [Fact]
    public void Changing_protected_admin_identity_is_blocked()
    {
        var admin = ProtectedAdmin();
        Assert.Throws<ProtectedAdminException>(() =>
            ProtectedAdminGuard.EnsureCanChangeIdentity(admin, "someone-else@widgetworks.demo", UserRoles.Administrator));
        Assert.Throws<ProtectedAdminException>(() =>
            ProtectedAdminGuard.EnsureCanChangeIdentity(admin, admin.Email, UserRoles.Manager));
    }

    [Fact]
    public void Same_identity_for_protected_admin_is_allowed()
    {
        var admin = ProtectedAdmin();
        ProtectedAdminGuard.EnsureCanChangeIdentity(admin, admin.Email.ToUpperInvariant(), UserRoles.Administrator);
    }

    [Fact]
    public void Ordinary_users_may_change_identity_freely()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "jane@example.com", Role = UserRoles.Customer, IsProtectedAdmin = false };
        ProtectedAdminGuard.EnsureCanChangeIdentity(user, "new@example.com", UserRoles.Manager); // does not throw
    }
}
