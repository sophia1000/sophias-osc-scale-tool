using VrcHeightOsc.App.Networking;
using VrcHeightOsc.Core.Domain;

namespace VrcHeightOsc.App;

internal interface IAppController : IAsyncDisposable
{
    AppStateSnapshot Snapshot { get; }
    NetworkStateSnapshot NetworkSnapshot { get; }
    event Action? StateChanged;

    Task StartAsync();
    Task SetHeightAsync(double height, bool smooth, double smoothSeconds);
    Task AddHeightAsync(double delta, bool smooth, double smoothSeconds);
    Task TestRuleAsync(int index);
    void AddRule();
    void UpdateRule(int index, RuleDefinition rule);
    void RemoveRule(int index);
    void UpdateUi(Action<UiConfig> mutation);
    void RequestHardReconnect();
    Task RefreshValuesAsync();
    Task SaveAsync();
}
