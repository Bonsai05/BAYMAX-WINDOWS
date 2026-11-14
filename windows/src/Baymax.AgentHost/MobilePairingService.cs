using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Baymax.Contracts;
using Baymax.Core.Persistence;

namespace Baymax.AgentHost;

public sealed class MobilePairingOptions
{
    public MobilePairingOptions(bool enabled, IPAddress? bindAddress, int port, X509Certificate2? certificate)
    {
        Enabled = enabled;
        BindAddress = bindAddress;
        Port = port;
        Certificate = certificate;
        CertificateSha256 = certificate is null
            ? null
            : Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public bool Enabled { get; }
    public IPAddress? BindAddress { get; }
    public int Port { get; }
    public X509Certificate2? Certificate { get; }
    public string? CertificateSha256 { get; }
    public string? Endpoint => Enabled && BindAddress is not null
        ? $"https://{BindAddress}:{Port}/mobile"
        : null;

    public static MobilePairingOptions FromConfiguration(
        IConfiguration configuration,
        int apiPort)
    {
        var section = configuration.GetSection("Baymax:Mobile");
        var enabled = section.GetValue("Enabled", false);
        var portText = section["Port"] ?? "5081";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Baymax:Mobile:Port must be from 1 to 65535; received '{portText}'.");
        }

        if (!enabled)
        {
            return new MobilePairingOptions(false, null, port, null);
        }

        var addressText = section["BindAddress"];
        if (!IPAddress.TryParse(addressText, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !IsPrivateIpv4(address)
            || !IsAssignedAddress(address))
        {
            throw new InvalidOperationException(
                "Baymax:Mobile:BindAddress must be a private IPv4 address assigned to an active network adapter on this computer.");
        }

        if (port == apiPort)
        {
            throw new InvalidOperationException("Baymax:Mobile:Port must differ from Baymax:Port.");
        }

        return new MobilePairingOptions(true, address, port, LoadOrCreateCertificate(address));
    }

    private static X509Certificate2 LoadOrCreateCertificate(IPAddress address)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The secure mobile TLS certificate is managed by the Windows user certificate store.");
        }

        var subjectName = $"CN=Baymax Mobile Host {address}";
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, subjectName, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificate =>
                certificate.HasPrivateKey
                && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30)
                && HasIpSubjectAlternativeName(certificate, address));
        if (existing is not null)
        {
            return new X509Certificate2(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var keyParameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
            ExportPolicy = CngExportPolicies.None
        };
        keyParameters.Parameters.Add(
            new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.Persist));
        using var cngKey = CngKey.Create(
            CngAlgorithm.Rsa,
            $"BaymaxMobile-{Guid.NewGuid():N}",
            keyParameters);
        using var rsa = new RSACng(cngKey);
        var request = new CertificateRequest(
            new X500DistinguishedName(subjectName),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var enhancedKeyUsage = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(enhancedKeyUsage, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(address);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));
        store.Add(certificate);
        return new X509Certificate2(certificate);
    }

    private static bool HasIpSubjectAlternativeName(X509Certificate2 certificate, IPAddress address)
    {
        var subjectAlternativeName = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();
        if (subjectAlternativeName is null)
        {
            return false;
        }

        return subjectAlternativeName.EnumerateIPAddresses().Contains(address);
    }

    private static bool IsAssignedAddress(IPAddress address) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}

public sealed class MobilePairingService(
    DevicePairingStore store,
    MobilePairingOptions options)
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PairingRequestLifetime = TimeSpan.FromMinutes(5);

    public PairingStatusResponse GetStatus() =>
        new(options.Enabled, options.Endpoint, options.CertificateSha256,
            options.Enabled ? null : "Mobile pairing is disabled.");

    public async Task<PairingInvitationResponse> CreateInvitationAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var now = DateTimeOffset.UtcNow;
        var invitationId = Guid.NewGuid();
        var secret = CreateSecret();
        var expiresAt = now.Add(InvitationLifetime);
        await store.CreateInvitationAsync(
            invitationId,
            DevicePairingStore.HashSecret(secret),
            now,
            expiresAt,
            cancellationToken);
        return new PairingInvitationResponse(
            new PairingQrPayload(
                1,
                options.Endpoint!,
                options.CertificateSha256!,
                invitationId,
                secret,
                Environment.MachineName),
            expiresAt);
    }

    public async Task<PairingRequestCreatedResponse> RegisterDeviceAsync(
        RegisterPairingDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        ValidateRequest(request);
        var publicKeyBytes = Convert.FromBase64String(request.PublicKey);
        var signatureBytes = Convert.FromBase64String(request.Signature);
        bool signatureIsValid;
        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            var curveOid = publicKey.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            signatureIsValid = bytesRead == publicKeyBytes.Length
                && publicKey.KeySize == 256
                && curveOid == "1.2.840.10045.3.1.7"
                && publicKey.VerifyData(
                    PairingProtocol.GetRegistrationMessage(request),
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            throw new PairingRejectedException("The device key proof is invalid.");
        }

        if (!signatureIsValid)
        {
            throw new PairingRejectedException("The device key proof is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var pollingSecret = CreateSecret();
        var created = await store.TryCreateRequestAsync(
            request.InvitationId,
            DevicePairingStore.HashSecret(request.Secret),
            requestId,
            request.DeviceName.Trim(),
            request.PublicKey,
            Convert.ToHexString(SHA256.HashData(publicKeyBytes)).ToLowerInvariant(),
            DevicePairingStore.HashSecret(pollingSecret),
            now,
            now.Add(PairingRequestLifetime),
            cancellationToken);
        if (!created)
        {
            throw new PairingRejectedException("The invitation is invalid, expired, or has already been used.");
        }

        return new PairingRequestCreatedResponse(
            requestId,
            pollingSecret,
            now.Add(PairingRequestLifetime));
    }

    public async Task<PairingRequestStatusResponse?> GetRequestStatusAsync(
        Guid requestId,
        string pollingSecret,
        CancellationToken cancellationToken = default) =>
        await store.GetRequestStatusAsync(
            requestId,
            DevicePairingStore.HashSecret(pollingSecret),
            DateTimeOffset.UtcNow,
            cancellationToken);

    public Task<IReadOnlyList<PendingPairingRequestResponse>> GetPendingRequestsAsync(
        CancellationToken cancellationToken = default) =>
        store.GetPendingRequestsAsync(DateTimeOffset.UtcNow, cancellationToken);

    public Task<string?> ConfirmRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        store.ConfirmRequestAsync(requestId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> RejectRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        store.RejectRequestAsync(requestId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> CancelInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) =>
        store.CancelInvitationAsync(invitationId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<IReadOnlyList<PairedDeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        store.GetDevicesAsync(cancellationToken);

    public Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        store.RevokeDeviceAsync(deviceId, DateTimeOffset.UtcNow, cancellationToken);

    private void EnsureEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Mobile pairing is disabled.");
        }
    }

    private static void ValidateRequest(RegisterPairingDeviceRequest request)
    {
        if (request.InvitationId == Guid.Empty
            || string.IsNullOrEmpty(request.Secret)
            || string.IsNullOrWhiteSpace(request.DeviceName)
            || string.IsNullOrEmpty(request.PublicKey)
            || string.IsNullOrEmpty(request.Signature)
            || request.Secret.Length is < 40 or > 64
            || request.DeviceName.Trim().Length is < 1 or > 64
            || request.DeviceName.Any(char.IsControl)
            || request.PublicKey.Length is < 80 or > 1024
            || request.Signature.Length is < 80 or > 256)
        {
            throw new PairingRejectedException("The pairing registration request is invalid.");
        }

        try
        {
            _ = Convert.FromBase64String(request.Secret);
            _ = Convert.FromBase64String(request.PublicKey);
            _ = Convert.FromBase64String(request.Signature);
        }
        catch (FormatException)
        {
            throw new PairingRejectedException("The pairing registration request is invalid.");
        }
    }

    private static string CreateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class PairingRejectedException(string message) : Exception(message);
