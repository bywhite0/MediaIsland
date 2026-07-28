using Windows.Media.Control;

namespace MediaIsland.Services.Media.Platform.Windows;

/// <summary>
/// SMTC-backed <see cref="IMediaPlaybackController"/>. Manual checklist (no WinRT in shared tests):
/// 1. No focused session → <see cref="MediaPlaybackCommandStatus.NoSession"/>
/// 2. Control flag disabled → <see cref="MediaPlaybackCommandStatus.NotSupported"/>;
///    <c>Try*Async</c> returns false → <see cref="MediaPlaybackCommandStatus.Failed"/>
/// 3. Successful play/pause/next/previous → <see cref="MediaPlaybackCommandStatus.Succeeded"/>
/// 4. WinRT exceptions are caught and mapped to Failed (never thrown to callers)
/// </summary>
public sealed class WindowsSmtcMediaPlaybackController : IMediaPlaybackController
{
    private readonly Func<GlobalSystemMediaTransportControlsSession?> _focusedSessionFactory;

    public WindowsSmtcMediaPlaybackController(WindowsSmtcMediaSessionProvider sessionProvider)
        : this(sessionProvider.GetFocusedControlSession)
    {
    }

    /// <summary>
    /// Test/DI hook: inject a focused-session factory without constructing SMTC manager.
    /// </summary>
    public WindowsSmtcMediaPlaybackController(
        Func<GlobalSystemMediaTransportControlsSession?> focusedSessionFactory)
    {
        _focusedSessionFactory = focusedSessionFactory
            ?? throw new ArgumentNullException(nameof(focusedSessionFactory));
    }

    public MediaPlaybackCapabilities Capabilities =>
        MediaPlaybackCapabilities.Play |
        MediaPlaybackCapabilities.Pause |
        MediaPlaybackCapabilities.Next |
        MediaPlaybackCapabilities.Previous;

    public async Task<MediaPlaybackCommandResult> ExecuteAsync(
        MediaPlaybackCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = _focusedSessionFactory();
            if (session is null)
            {
                return new MediaPlaybackCommandResult(
                    MediaPlaybackCommandStatus.NoSession,
                    "no focused SMTC session");
            }

            if (!IsCommandEnabled(session, command))
            {
                return new MediaPlaybackCommandResult(
                    MediaPlaybackCommandStatus.NotSupported,
                    $"SMTC control does not enable {command}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var ok = command switch
            {
                MediaPlaybackCommand.Play => await session.TryPlayAsync(),
                MediaPlaybackCommand.Pause => await session.TryPauseAsync(),
                MediaPlaybackCommand.Next => await session.TrySkipNextAsync(),
                MediaPlaybackCommand.Previous => await session.TrySkipPreviousAsync(),
                _ => false
            };

            return ok
                ? new MediaPlaybackCommandResult(MediaPlaybackCommandStatus.Succeeded)
                : new MediaPlaybackCommandResult(
                    MediaPlaybackCommandStatus.Failed,
                    "SMTC Try* returned false");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MediaPlaybackCommandResult(
                MediaPlaybackCommandStatus.Failed,
                ex.Message);
        }
    }

    private static bool IsCommandEnabled(
        GlobalSystemMediaTransportControlsSession session,
        MediaPlaybackCommand command)
    {
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            if (controls is null)
            {
                // Missing control metadata: still attempt Try* and let result decide.
                return true;
            }

            return command switch
            {
                MediaPlaybackCommand.Play => controls.IsPlayEnabled,
                MediaPlaybackCommand.Pause => controls.IsPauseEnabled,
                MediaPlaybackCommand.Next => controls.IsNextEnabled,
                MediaPlaybackCommand.Previous => controls.IsPreviousEnabled,
                _ => false
            };
        }
        catch
        {
            // If Controls cannot be read, fall through to Try* path.
            return true;
        }
    }
}
