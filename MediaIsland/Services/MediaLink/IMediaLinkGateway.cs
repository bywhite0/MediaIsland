using System.ComponentModel;

namespace MediaIsland.Services.MediaLink;

public interface IMediaLinkGateway : INotifyPropertyChanged
{
    bool IsRunning { get; }

    string? Endpoint { get; }

    string? LastError { get; }

    int ActiveSessionCount { get; }
}