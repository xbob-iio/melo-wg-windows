#if DEBUG
using System;

namespace MeloWG.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Только для отрисовки превью интерфейса: изображает активное подключение без запуска ядра.</summary>
    internal void PreviewConnected(TimeSpan elapsed, long up, long down, long upTotal, long downTotal, bool pending)
    {
        _appliedKey = pending ? "preview-old-settings" : CurrentKey();
        _connectedTunnelName = SelectedTunnel?.Name;
        _connectedAt = DateTime.UtcNow - elapsed;
        State = ConnState.Connected;
        _upSpeed = up;
        _downSpeed = down;
        _upTotal = upTotal;
        _downTotal = downTotal;
        NotifyTraffic();
        UpdatePending();
    }
}
#endif
