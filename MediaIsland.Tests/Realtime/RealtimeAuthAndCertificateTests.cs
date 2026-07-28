using MediaIsland.Services.Realtime;
using Xunit;

namespace MediaIsland.Tests.Realtime;

public class RealtimeAuthTests
{
    [Fact]
    public void ValidateToken_RejectsNullOrEmpty()
    {
        Assert.False(RealtimeAuth.ValidateToken(null, "a"));
        Assert.False(RealtimeAuth.ValidateToken("a", null));
        Assert.False(RealtimeAuth.ValidateToken("", "a"));
        Assert.False(RealtimeAuth.ValidateToken("a", ""));
        Assert.False(RealtimeAuth.ValidateToken(null, null));
    }

    [Fact]
    public void ValidateToken_AcceptsExactMatch_RejectsMismatch()
    {
        Assert.True(RealtimeAuth.ValidateToken("secret-token", "secret-token"));
        Assert.False(RealtimeAuth.ValidateToken("secret-token", "secret-tokem"));
        Assert.False(RealtimeAuth.ValidateToken("short", "longer-value"));
    }

    [Fact]
    public void GenerateToken_IsNonEmpty_AndValidatesAgainstItself()
    {
        var token = RealtimeAuth.GenerateToken();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(RealtimeAuth.ValidateToken(token, token));
        Assert.False(RealtimeAuth.ValidateToken(token, token + "x"));
    }
}

public class RealtimeCertificateStoreTests
{
    [Fact]
    public void EnsureCertificate_GeneratesAndReloadsSameFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MediaIslandRealtimeCertTests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new RealtimeCertificateStore(directory);
            var cert1 = first.EnsureCertificate();
            var fingerprint1 = first.CertFingerprint;
            Assert.False(string.IsNullOrWhiteSpace(fingerprint1));
            Assert.Equal(64, fingerprint1.Length);
            Assert.Equal(fingerprint1, RealtimeCertificateStore.ComputeFingerprint(cert1));
            Assert.True(File.Exists(first.PfxPath));
            Assert.Contains("MediaIsland-Realtime", cert1.Subject, StringComparison.Ordinal);

            var second = new RealtimeCertificateStore(directory);
            var cert2 = second.EnsureCertificate();
            Assert.Equal(fingerprint1, second.CertFingerprint);
            Assert.Equal(cert1.Thumbprint, cert2.Thumbprint);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
