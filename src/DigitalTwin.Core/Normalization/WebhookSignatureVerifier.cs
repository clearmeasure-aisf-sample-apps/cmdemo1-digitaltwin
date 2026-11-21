using System.Security.Cryptography;
using System.Text;

namespace DigitalTwin.Core.Normalization;

public static class WebhookSignatureVerifier
{
    public static bool VerifyGitHubSignature(ReadOnlySpan<byte> body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hexPart = signatureHeader.AsSpan(prefix.Length);
        if (hexPart.Length != 64)
        {
            return false;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(hexPart);
        }
        catch (FormatException)
        {
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var actualBytes = HMACSHA256.HashData(keyBytes, body);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    public static bool VerifySharedSecret(string? providedSecret, string expectedSecret)
    {
        if (string.IsNullOrEmpty(expectedSecret) || string.IsNullOrWhiteSpace(providedSecret))
        {
            return false;
        }

        var normalized = providedSecret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? providedSecret["Bearer ".Length..].Trim()
            : providedSecret.Trim();

        var providedBytes = Encoding.UTF8.GetBytes(normalized);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
