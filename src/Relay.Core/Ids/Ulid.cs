using System.Security.Cryptography;

namespace Relay.Core.Ids;

/// <summary>
/// ULID generator (https://github.com/ulid/spec): 48-bit millisecond timestamp followed by
/// 80 bits of cryptographic randomness, encoded as 26 Crockford base32 characters.
/// IDs are lexically sortable by creation time and never collide in practice.
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = now.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 26) return false;
        foreach (var c in value)
        {
            if (Alphabet.IndexOf(char.ToUpperInvariant(c)) < 0) return false;
        }
        return true;
    }

    private static string Encode(ReadOnlySpan<byte> b)
    {
        // 128 bits -> 26 chars (130 bits, top 2 bits zero).
        Span<char> c = stackalloc char[26];
        // Timestamp (10 chars from 48 bits)
        c[0] = Alphabet[(b[0] & 224) >> 5];
        c[1] = Alphabet[b[0] & 31];
        c[2] = Alphabet[(b[1] & 248) >> 3];
        c[3] = Alphabet[((b[1] & 7) << 2) | ((b[2] & 192) >> 6)];
        c[4] = Alphabet[(b[2] & 62) >> 1];
        c[5] = Alphabet[((b[2] & 1) << 4) | ((b[3] & 240) >> 4)];
        c[6] = Alphabet[((b[3] & 15) << 1) | ((b[4] & 128) >> 7)];
        c[7] = Alphabet[(b[4] & 124) >> 2];
        c[8] = Alphabet[((b[4] & 3) << 3) | ((b[5] & 224) >> 5)];
        c[9] = Alphabet[b[5] & 31];
        // Randomness (16 chars from 80 bits)
        c[10] = Alphabet[(b[6] & 248) >> 3];
        c[11] = Alphabet[((b[6] & 7) << 2) | ((b[7] & 192) >> 6)];
        c[12] = Alphabet[(b[7] & 62) >> 1];
        c[13] = Alphabet[((b[7] & 1) << 4) | ((b[8] & 240) >> 4)];
        c[14] = Alphabet[((b[8] & 15) << 1) | ((b[9] & 128) >> 7)];
        c[15] = Alphabet[(b[9] & 124) >> 2];
        c[16] = Alphabet[((b[9] & 3) << 3) | ((b[10] & 224) >> 5)];
        c[17] = Alphabet[b[10] & 31];
        c[18] = Alphabet[(b[11] & 248) >> 3];
        c[19] = Alphabet[((b[11] & 7) << 2) | ((b[12] & 192) >> 6)];
        c[20] = Alphabet[(b[12] & 62) >> 1];
        c[21] = Alphabet[((b[12] & 1) << 4) | ((b[13] & 240) >> 4)];
        c[22] = Alphabet[((b[13] & 15) << 1) | ((b[14] & 128) >> 7)];
        c[23] = Alphabet[(b[14] & 124) >> 2];
        c[24] = Alphabet[((b[14] & 3) << 3) | ((b[15] & 224) >> 5)];
        c[25] = Alphabet[b[15] & 31];
        return new string(c);
    }
}
