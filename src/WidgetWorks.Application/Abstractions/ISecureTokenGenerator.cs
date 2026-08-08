namespace WidgetWorks.Application.Abstractions;

/// <summary>Generates cryptographically-random opaque tokens and hashes them for at-rest storage.</summary>
public interface ISecureTokenGenerator
{
    string Generate();

    string Hash(string rawToken);
}
