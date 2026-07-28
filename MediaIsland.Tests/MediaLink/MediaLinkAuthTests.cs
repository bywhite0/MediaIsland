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
    }

    [Fact]
    public void ValidateToken_AcceptsExactMatch_RejectsMismatch()
    {
        Assert.True(MediaLinkAuth.ValidateToken("secret-token", "secret-token"));
        Assert.False(MediaLinkAuth.ValidateToken("secret-token", "secret-tokem"));
    }

    [Fact]
    public void GenerateToken_IsUrlSafe_AndSelfValidates()
    {
        var token = MediaLinkAuth.GenerateToken();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(MediaLinkAuth.ValidateToken(token, token));
    }
}
