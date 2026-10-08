using WidgetWorks.Domain.Users;
using Xunit;

namespace WidgetWorks.UnitTests;

public class DisplayNamePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Blank_becomes_null_rather_than_an_empty_string(string? input)
    {
        // Not a nicety: the database's check constraint rejects a blank name outright, and the
        // greeting only falls back to the neutral wording on null. An empty string would fail the
        // write in one place and render "Hello, " in the other.
        Assert.Null(DisplayNamePolicy.Normalize(input));
    }

    [Fact]
    public void Surrounding_whitespace_is_removed()
    {
        Assert.Equal("Jane Doe", DisplayNamePolicy.Normalize("  Jane Doe  "));
    }

    [Fact]
    public void A_name_at_the_limit_is_accepted()
    {
        var name = new string('a', DisplayNamePolicy.MaxLength);
        Assert.False(DisplayNamePolicy.IsTooLong(name));
    }

    [Fact]
    public void A_name_past_the_limit_is_rejected()
    {
        var name = new string('a', DisplayNamePolicy.MaxLength + 1);
        Assert.True(DisplayNamePolicy.IsTooLong(name));
        Assert.Contains($"{DisplayNamePolicy.MaxLength} characters", DisplayNamePolicy.TooLongMessage);
    }

    [Fact]
    public void A_null_name_is_not_too_long()
    {
        Assert.False(DisplayNamePolicy.IsTooLong(null));
    }

    [Fact]
    public void A_provider_name_past_the_limit_is_truncated_rather_than_refused()
    {
        // The difference that matters between the two entry points. Someone typing a name can be
        // told it is too long; someone signing in with Google cannot, because there is no form in
        // front of them — refusing the login over a greeting would be the wrong trade.
        var long_name = new string('a', DisplayNamePolicy.MaxLength + 20);

        var result = DisplayNamePolicy.FromProvider(long_name);

        Assert.NotNull(result);
        Assert.Equal(DisplayNamePolicy.MaxLength, result!.Length);
    }

    [Fact]
    public void Truncation_does_not_leave_a_trailing_space()
    {
        // A cut landing immediately after a space would otherwise produce "Firstname " in the
        // header. Normalize has already stripped leading whitespace, so trimming the tail cannot
        // empty the string.
        var name = new string('a', DisplayNamePolicy.MaxLength - 1) + "   tail";

        var result = DisplayNamePolicy.FromProvider(name);

        Assert.NotNull(result);
        Assert.Equal(new string('a', DisplayNamePolicy.MaxLength - 1), result);
    }

    [Fact]
    public void A_provider_name_within_the_limit_is_returned_unchanged()
    {
        Assert.Equal("Jane Doe", DisplayNamePolicy.FromProvider("  Jane Doe  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_provider_that_sends_no_name_yields_null(string? input)
    {
        Assert.Null(DisplayNamePolicy.FromProvider(input));
    }
}
