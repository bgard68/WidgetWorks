using WidgetWorks.Domain.Users;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The password rules themselves.
///
/// Worth its own suite for two reasons. The policy is the one place the rules live, and both
/// registration and password reset defer to it — a rule that quietly stopped working here would
/// weaken both paths at once with nothing failing. And the web client mirrors this list to tick
/// requirements off as someone types, so the exact set and wording are a contract, not an
/// implementation detail: a checklist that disagrees with the server tells people they are done when
/// they are not.
/// </summary>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Str0ng!Passw0rd")]
    [InlineData("DemoAdmin!Change01")]      // the seeded demo accounts
    [InlineData("DemoUser!Change01")]
    [InlineData("ApiSuite!Change01")]       // the API suite's fixture password
    [InlineData("correct horse Battery 9!")] // a passphrase, spaces and all
    public void A_password_that_satisfies_every_rule_is_accepted(string password)
    {
        Assert.True(PasswordPolicy.IsAcceptable(password));
        Assert.Empty(PasswordPolicy.Unmet(password));
        Assert.Equal(string.Empty, PasswordPolicy.Describe(password));
    }

    [Theory]
    [InlineData("Sh0rt!1", "length")]                   // 7 characters
    [InlineData("STR0NG!PASSWORD", "lowercase")]
    [InlineData("str0ng!password", "uppercase")]
    [InlineData("Strong!Password", "digit")]
    [InlineData("Str0ngPassw0rd", "symbol")]
    public void Each_rule_is_the_only_thing_standing_between_a_password_and_acceptance(string password, string expected)
    {
        // One rule short in each case, so a rule that stopped being evaluated would show up here as a
        // password that should have been refused and was not.
        var unmet = PasswordPolicy.Unmet(password);

        Assert.Equal(expected, Assert.Single(unmet).Code);
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_fails_every_rule_rather_than_being_special_cased(string? password)
    {
        // More useful than one generic complaint, and true: an empty password satisfies none of them.
        Assert.Equal(PasswordPolicy.Rules.Count, PasswordPolicy.Unmet(password).Count);
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void The_refusal_names_what_is_missing_and_nothing_else()
    {
        var description = PasswordPolicy.Describe("short");

        // Four of the five rules, named — something to act on rather than the whole policy restated.
        Assert.Equal("Password needs: at least 10 characters, an uppercase letter, a number, a symbol, such as ! ? # or @.", description);

        // And it says nothing about the password itself, so it is safe to put in an API response.
        Assert.DoesNotContain("short", description);
    }

    [Fact]
    public void Surrounding_spaces_are_kept_because_they_are_part_of_the_password()
    {
        // Trimming would silently change what someone typed and then fail to match it at login. The
        // spaces count toward length, as they do in any password store worth using.
        Assert.True(PasswordPolicy.IsAcceptable("  Ab1!cdef  "));
    }

    [Fact]
    public void The_rules_are_a_stable_contract_the_web_client_mirrors()
    {
        // web/src/lib/passwordPolicy.ts carries the same five codes in the same order so the checklist
        // matches what the server will actually do. If this assertion is what broke your build, the
        // other file needs the same edit.
        Assert.Equal(
            ["length", "lowercase", "uppercase", "digit", "symbol"],
            PasswordPolicy.Rules.Select(r => r.Code));

        Assert.Equal(10, PasswordPolicy.MinimumLength);
        Assert.All(PasswordPolicy.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Requirement)));
    }
}
