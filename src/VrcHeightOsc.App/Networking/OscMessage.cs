namespace VrcHeightOsc.App.Networking;

internal sealed record OscMessage(string Address, IReadOnlyList<object?> Arguments)
{
    public object? Value => Arguments.Count switch
    {
        0 => null,
        1 => Arguments[0],
        _ => Arguments.ToArray(),
    };
}
