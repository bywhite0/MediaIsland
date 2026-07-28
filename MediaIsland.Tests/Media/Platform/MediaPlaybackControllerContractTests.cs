using MediaIsland.Services.Media.Platform;
using Xunit;

namespace MediaIsland.Tests.Media.Platform;

public class MediaPlaybackControllerContractTests
{
    [Fact]
    public void NoOpProvider_PlaybackController_IsNull()
    {
        var provider = new NoOpMediaPlatformProvider();
        Assert.Null(provider.PlaybackController);
    }

    [Fact]
    public async Task FakeController_Execute_ReturnsStatus()
    {
        IMediaPlaybackController controller = new FakePlaybackController(
            MediaPlaybackCommandStatus.Succeeded);
        var result = await controller.ExecuteAsync(MediaPlaybackCommand.Play);
        Assert.Equal(MediaPlaybackCommandStatus.Succeeded, result.Status);
    }

    private sealed class FakePlaybackController(MediaPlaybackCommandStatus status) : IMediaPlaybackController
    {
        public MediaPlaybackCapabilities Capabilities =>
            MediaPlaybackCapabilities.Play | MediaPlaybackCapabilities.Pause |
            MediaPlaybackCapabilities.Next | MediaPlaybackCapabilities.Previous;

        public Task<MediaPlaybackCommandResult> ExecuteAsync(
            MediaPlaybackCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaPlaybackCommandResult(status));
    }
}
