namespace WidgetWorks.Application.Abstractions;

public sealed record RecoveryCode(string Plain, string Hash);

public interface IRecoveryCodes
{
    /// <summary>Generates single-use recovery codes: plaintext (shown once) + hash (stored).</summary>
    IReadOnlyList<RecoveryCode> Generate(int count);

    /// <summary>Hashes a submitted recovery code for comparison against stored hashes.</summary>
    string Hash(string code);
}
