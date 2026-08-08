using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.UnitTests.Fakes;

public sealed class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    public GoogleIdentity? Result { get; set; }

    public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct) => Task.FromResult(Result);
}
