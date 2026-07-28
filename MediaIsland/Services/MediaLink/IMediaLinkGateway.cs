namespace MediaIsland.Services.MediaLink;

public interface IMediaLinkGateway
{
    bool IsRunning { get; }

    string? Endpoint { get; }

    string? CertFingerprint { get; }

    string? LastError { get; }
}
