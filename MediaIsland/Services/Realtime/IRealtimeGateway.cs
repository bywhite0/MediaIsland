namespace MediaIsland.Services.Realtime;

public interface IRealtimeGateway
{
    bool IsRunning { get; }

    string? Endpoint { get; }

    string? CertFingerprint { get; }

    string? LastError { get; }
}
