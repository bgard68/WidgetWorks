using System.Reflection;
using NetArchTest.Rules;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;
using WidgetWorks.Infrastructure.Persistence;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The onion's arrows, pinned as tests. The layering (Domain ← Application ← Infrastructure, with
/// the WebApi on the rim) is carried by project references today, but a project reference only
/// stops the dependencies nobody added: one convenient reference tomorrow and a handler is talking
/// to Npgsql directly. These rules fail that pull request instead, and name the types that crossed.
///
/// WebApi is deliberately not scanned. It is the composition root, so it references every layer to
/// wire the container — pointing outward is its job, and there is no direction left for it to get
/// wrong.
/// </summary>
public class ArchitectureTests
{
    // Each assembly is anchored by a real type it contains, so a rename or a project split breaks
    // the compile here rather than leaving the rules silently scanning nothing.
    private static readonly Assembly DomainAssembly = typeof(Result).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IUserRepository).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(UserRepository).Assembly;

    [Fact]
    public void The_domain_depends_on_no_other_layer()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "WidgetWorks.Application",
                "WidgetWorks.Infrastructure",
                "WidgetWorks.WebApi")
            .GetResult();

        MustHold(result, "Domain must not reach outward to any other layer");
    }

    [Fact]
    public void The_domain_takes_nothing_beyond_the_base_class_library()
    {
        // The Domain project carries zero package references on purpose: entities and rules compile
        // against the BCL alone, which is what keeps them testable with nothing but xUnit. This is
        // the ban-list version of that fact — persistence, web and container libraries by name.
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Npgsql",
                "Dapper",
                "DbUp",
                "System.Data",
                "Microsoft.Data",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions")
            .GetResult();

        MustHold(result, "Domain must stay free of persistence, web and container libraries");
    }

    [Fact]
    public void Application_code_reaches_only_inward()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOnAny("WidgetWorks.Infrastructure", "WidgetWorks.WebApi")
            .GetResult();

        MustHold(result, "Application must depend on Domain only, never outward");
    }

    [Fact]
    public void Application_code_stays_behind_its_ports()
    {
        // The SQL, the web framework and the provider-specific security libraries all live behind
        // the ports in Abstractions/; Infrastructure implements them. Microsoft.Extensions is
        // deliberately absent from this list: the DI and logging *abstractions* are the two pieces
        // of framework the layer does consume (AddApplication() registration and ILogger), and
        // that is the real design, not an oversight.
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Npgsql",
                "Dapper",
                "DbUp",
                "System.Data",
                "Microsoft.Data",
                "Microsoft.AspNetCore",
                "Microsoft.IdentityModel",
                "BCrypt.Net",
                "OtpNet")
            .GetResult();

        MustHold(result, "Application must go through its ports, not persistence/web/crypto libraries");
    }

    [Fact]
    public void Infrastructure_never_references_the_web_host()
    {
        // Adapters must stay hostable from anywhere — the test suites load them with no web server
        // in sight. The one web-shaped thing Infrastructure does (SMTP email) is System.Net.Mail,
        // not ASP.NET, so a dependency on the host or its framework can only be an accident.
        var result = Types.InAssembly(InfrastructureAssembly)
            .Should()
            .NotHaveDependencyOnAny("WidgetWorks.WebApi", "Microsoft.AspNetCore")
            .GetResult();

        MustHold(result, "Infrastructure must not depend on the web host");
    }

    private static void MustHold(TestResult result, string rule)
    {
        // FailingTypes names the offenders; without it a red arrow test says "the architecture is
        // wrong somewhere", which helps nobody.
        var offenders = result.FailingTypes is { Count: > 0 }
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "(none)";

        Assert.True(result.IsSuccessful, $"{rule}. Offending types: {offenders}");
    }
}
