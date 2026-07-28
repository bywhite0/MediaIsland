using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkAuthTests
{
    [Fact]
    public void ValidateToken_RejectsNullOrEmpty()
    {
        Assert.False(MediaLinkAuth.ValidateToken(null, "a"));
        Assert.False(MediaLinkAuth.ValidateToken("a", null));
        Assert.False(MediaLinkAuth.ValidateToken("", "a"));
        Assert.False(MediaLinkAuth.ValidateToken("a", ""));
        Assert.False(MediaLinkAuth.ValidateToken(null, null));
    }

    [Fact]
    public void ValidateToken_AcceptsExactMatch_RejectsMismatch()
    {
        Assert.True(MediaLinkAuth.ValidateToken("secret-token", "secret-token"));
        Assert.False(MediaLinkAuth.ValidateToken("secret-token", "secret-tokem"));
        Assert.False(MediaLinkAuth.ValidateToken("short", "longer-value"));
    }

    [Fact]
    public void GenerateToken_IsNonEmpty_AndValidatesAgainstItself()
    {
        var token = MediaLinkAuth.GenerateToken();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(MediaLinkAuth.ValidateToken(token, token));
        Assert.False(MediaLinkAuth.ValidateToken(token, token + "x"));
    }
}

public class MediaLinkCertificateStoreTests
{
    [Fact]
    public void EnsureCertificate_GeneratesAndReloadsSameFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MediaIslandMediaLinkCertTests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new MediaLinkCertificateStore(directory);
            var cert1 = first.EnsureCertificate();
            var fingerprint1 = first.CertFingerprint;
            Assert.False(string.IsNullOrWhiteSpace(fingerprint1));
            Assert.Equal(64, fingerprint1.Length);
            Assert.Equal(fingerprint1, MediaLinkCertificateStore.ComputeFingerprint(cert1));
            Assert.True(File.Exists(first.PfxPath));
            Assert.Contains("MediaIsland-MediaLink", cert1.Subject, StringComparison.Ordinal);

            var second = new MediaLinkCertificateStore(directory);
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
