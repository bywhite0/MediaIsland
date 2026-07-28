using System.Security.Cryptography;
using System.Text;

namespace MediaIsland.Services.Realtime;

public static class RealtimeAuth
{
    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static bool ValidateToken(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        if (expectedBytes.Length != providedBytes.Length)
        {
            // Still compare equal-length buffers to reduce trivial timing leaks on length.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        var base64 = Convert.ToBase64String(data);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
