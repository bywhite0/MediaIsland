using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MediaIsland.Services.Realtime;

public sealed class RealtimeCertificateStore
{
    // Application-fixed entropy for PKCS#12 export (KISS; file lives under user plugin config).
    private const string PfxPassword = "MediaIsland.Realtime.Pfx.v1";

    private readonly string _directory;
    private readonly string _pfxPath;
    private X509Certificate2? _certificate;
    private string? _fingerprint;

    public RealtimeCertificateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _directory = dataDirectory;
        _pfxPath = Path.Combine(_directory, "server.pfx");
    }

    public string PfxPath => _pfxPath;

    public string CertFingerprint =>
        _fingerprint ?? throw new InvalidOperationException("Certificate has not been loaded. Call EnsureCertificate first.");

    public X509Certificate2 Certificate =>
        _certificate ?? throw new InvalidOperationException("Certificate has not been loaded. Call EnsureCertificate first.");

    public X509Certificate2 EnsureCertificate()
    {
        Directory.CreateDirectory(_directory);

        if (_certificate is not null)
        {
            return _certificate;
        }

        if (File.Exists(_pfxPath))
        {
            LoadFromDisk();
            return _certificate!;
        }

        GenerateAndSave();
        return _certificate!;
    }

    public static string ComputeFingerprint(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void LoadFromDisk()
    {
        var pfxBytes = File.ReadAllBytes(_pfxPath);
#pragma warning disable SYSLIB0057 // X509Certificate2 PFX ctor is the net8-compatible load path
        var certificate = new X509Certificate2(
            pfxBytes,
            PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        SetCertificate(certificate);
    }

    private void GenerateAndSave()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=MediaIsland-Realtime",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(2);
        using var created = request.CreateSelfSigned(notBefore, notAfter);

        var pfxBytes = created.Export(X509ContentType.Pfx, PfxPassword);
        File.WriteAllBytes(_pfxPath, pfxBytes);

#pragma warning disable SYSLIB0057
        var reloaded = new X509Certificate2(
            pfxBytes,
            PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        SetCertificate(reloaded);
    }

    private void SetCertificate(X509Certificate2 certificate)
    {
        _certificate?.Dispose();
        _certificate = certificate;
        _fingerprint = ComputeFingerprint(certificate);
    }
}
