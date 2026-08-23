namespace VrcHeightOsc.App.Networking;

internal sealed record NetworkStateSnapshot(
    bool Connected,
    string? QueryHost,
    int? QueryPort,
    string OscHost,
    int OscPort,
    int LocalOscPort,
    int LocalQueryPort,
    int Generation,
    int RestartCount,
    string Status);
