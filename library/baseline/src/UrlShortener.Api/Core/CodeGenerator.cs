// v1 baseline (known issues: DEF-1 / DEF-2)
// DEF-2 (CWE-338): codes come from System.Random (the C# equivalent of Math.random) instead of a CSPRNG,
// so they are predictable and links can be enumerated.
namespace UrlShortener.Api.Core;

public sealed class CodeGenerator
{
    public const int DefaultLength = 7;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public string Generate(int length = DefaultLength)
    {
        if (length < 1 || length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        }

        return new string(chars);
    }
}
