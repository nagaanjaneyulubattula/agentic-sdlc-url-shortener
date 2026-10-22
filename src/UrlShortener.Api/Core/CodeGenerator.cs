using System.Security.Cryptography;

namespace UrlShortener.Api.Core;

/// <summary>Generates short codes (base62) from the operating system CSPRNG. Never use a non-cryptographic RNG here.</summary>
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

        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
