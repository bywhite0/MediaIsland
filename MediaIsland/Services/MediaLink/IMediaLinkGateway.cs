namespace MediaIsland.Services.MediaLink;

public interface IMediaLinkGateway
{
    bool IsRunning { get; }

    string? Endpoint { get; }

    string? LastError { get; }
}
