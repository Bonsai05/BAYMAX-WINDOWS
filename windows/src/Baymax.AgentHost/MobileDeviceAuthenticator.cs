using System.Globalization;
using System.Security.Cryptography;
using Baymax.Contracts;
using Baymax.Core.Persistence;

namespace Baymax.AgentHost;

public sealed record AuthenticatedMobileRequest(Guid DeviceId, byte[] Body);

public sealed class MobileRequestTooLargeException() : Exception("The mobile request body exceeds the allowed size.");

public sealed class MobileDeviceAuthenticator(DevicePairingStore store)
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumBodyBytes = 8192;

    public async Task<AuthenticatedMobileRequest?> AuthenticateAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSingleHeader(request, "X-Baymax-Device-Id", out var deviceIdText)
            || !Guid.TryParseExact(deviceIdText, "D", out var deviceId)
            || !TryGetSingleHeader(request, "X-Baymax-Timestamp", out var timestampText)
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !TryGetSingleHeader(request, "X-Baymax-Nonce", out var nonce)
            || !IsValidNonce(nonce)
            || !TryGetSingleHeader(request, "X-Baymax-Signature", out var signatureText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var nowSeconds = now.ToUnixTimeSeconds();
        var skewSeconds = (long)AllowedClockSkew.TotalSeconds;
        if (timestamp < nowSeconds - skewSeconds || timestamp > nowSeconds + skewSeconds)
        {
            return null;
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        var encodedPublicKey = await store.GetActiveDevicePublicKeyAsync(deviceId, cancellationToken);
        if (encodedPublicKey is null)
        {
            return null;
        }

        byte[] publicKeyBytes;
        byte[] signatureBytes;
        try
        {
            publicKeyBytes = Convert.FromBase64String(encodedPublicKey);
            signatureBytes = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            return null;
        }

        bool signatureIsValid;
        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            signatureIsValid = bytesRead == publicKeyBytes.Length
                && publicKey.KeySize == 256
                && publicKey.VerifyData(
                    PairingProtocol.GetAuthenticatedRequestMessage(
                        request.Method,
                        $"{request.Path.Value}{request.QueryString.Value}",
                        deviceId,
                        timestamp,
                        nonce,
                        body),
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (!signatureIsValid)
        {
            return null;
        }

        var nonceAccepted = await store.TryConsumeRequestNonceAsync(
            deviceId,
            nonce,
            request.Method,
            request.Path.Value ?? "/",
            now,
            now.Add(NonceLifetime),
            cancellationToken);
        return nonceAccepted ? new AuthenticatedMobileRequest(deviceId, body) : null;
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumBodyBytes)
        {
            throw new MobileRequestTooLargeException();
        }

        await using var body = new MemoryStream();
        var buffer = new byte[2048];
        while (true)
        {
            var bytesRead = await request.Body.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return body.ToArray();
            }

            if (body.Length + bytesRead > MaximumBodyBytes)
            {
                throw new MobileRequestTooLargeException();
            }

            await body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static bool TryGetSingleHeader(HttpRequest request, string name, out string value)
    {
        var values = request.Headers[name];
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return values.Count == 1 && value.Length > 0;
    }

    private static bool IsValidNonce(string value) =>
        value.Length is >= 22 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');
}
