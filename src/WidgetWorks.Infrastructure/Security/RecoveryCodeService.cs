using System.Security.Cryptography;
using System.Text;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Security;

public sealed class RecoveryCodeService : IRecoveryCodes
{
    public IReadOnlyList<RecoveryCode> Generate(int count)
    {
        var codes = new List<RecoveryCode>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            codes.Add(new RecoveryCode(raw, Hash(raw)));
        }

        return codes;
    }

    public string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
