using System.Security.Cryptography;

namespace Baymax.Contracts;

public sealed record PairingStatusResponse(
    bool Enabled,
    string? Endpoint,
    string? CertificateSha256,
    string? Message);

public sealed record PairingQrPayload(
    int Version,
    string Endpoint,
    string CertificateSha256,
    Guid InvitationId,
    string Secret,
    string HostName);

public sealed record PairingInvitationResponse(
    PairingQrPayload Payload,
    DateTimeOffset ExpiresAt);

public sealed record RegisterPairingDeviceRequest(
    Guid InvitationId,
    string Secret,
    string DeviceName,
    string PublicKey,
    string Signature);

public sealed record PairingRequestCreatedResponse(
    Guid RequestId,
    string PollingSecret,
    DateTimeOffset ExpiresAt);

public sealed record PairingRequestStatusResponse(
    string Status,
    string? DeviceId,
    DateTimeOffset ExpiresAt);

public sealed record PendingPairingRequestResponse(
    Guid RequestId,
    string DeviceName,
    string PublicKeySha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record PairedDeviceResponse(
    string DeviceId,
    string DeviceName,
    string PublicKeySha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record MobileSubmitTaskRequest(
    string UserInstruction,
    string IdempotencyKey);

public static class PairingProtocol
{
    public static byte[] GetRegistrationMessage(RegisterPairingDeviceRequest request)
    {
        var canonicalMessage = string.Join(
            '\n',
            "BAYMAX-PAIRING-REGISTRATION-V1",
            request.InvitationId.ToString("D").ToLowerInvariant(),
            request.Secret,
            request.DeviceName,
            request.PublicKey);
        return System.Text.Encoding.UTF8.GetBytes(canonicalMessage);
    }

    public static byte[] GetAuthenticatedRequestMessage(
        string method,
        string pathAndQuery,
        Guid deviceId,
        long timestamp,
        string nonce,
        ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonicalMessage = string.Join(
            '\n',
            "BAYMAX-MOBILE-REQUEST-V1",
            method.ToUpperInvariant(),
            pathAndQuery,
            deviceId.ToString("D").ToLowerInvariant(),
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            bodyHash);
        return System.Text.Encoding.UTF8.GetBytes(canonicalMessage);
    }
}
