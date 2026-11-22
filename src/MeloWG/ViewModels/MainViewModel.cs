using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using MeloWG.Core;
using MeloWG.Services;

namespace MeloWG.ViewModels;

public enum ConnState { Disconnected, Connecting, Connected, Disconnecting }

public sealed class TunnelItem : ObservableObject
{
    private bool _isSelected;

    public TunnelItem(TunnelRecord record) => Record = record;

    public TunnelRecord Record { get; }
    public string Id => Record.Id;
    public string Name => Record.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class AppItem
{
    public AppItem(AppEntry entry)
    {
        Entry = entry;
        Icon = IconCache.Get(entry.Path);
    }

    public AppEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path ?? "добавлено по имени файла";
    public ImageSource Icon { get; }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly Storage _storage;
    private readonly CoreRunner _core;
    private readonly TrafficMonitor _traffic;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _clock;
    private readonly AppSettings _settings;
    private readonly List<string> _log = new();
    private readonly List<string> _pendingLog = new();
    private readonly DispatcherTimer _logFlush;

    private ConnState _state;
    private TunnelItem? _selectedTunnel;
    private string _selectedEndpoint = "";
    private string? _lastError;
    private DateTime _connectedAt;
    private long _upSpeed, _downSpeed, _upTotal, _downTotal;
    private string? _appliedKey;
    private string? _connectedTunnelName;

    public MainViewModel(Storage storage, CoreRunner core, TrafficMonitor traffic, Dispatcher ui)
    {
        _storage = storage;
        _core = core;
        _traffic = traffic;
        _ui = ui;
        _settings = storage.LoadSettings();

        _clock = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(StatusDetail));

        // Журнал уходит в окно пачками: сотни строк в секунду по одной заметно тормозили бы интерфейс.
        _logFlush = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(200) };
        _logFlush.Tick += (_, _) => FlushLog();
        _logFlush.Start();

        _core.LogLine += line => AppendLog(line);
        _core.Crashed += reason => _ui.BeginInvoke(() => OnCoreCrashed(reason));
        _traffic.Sample += (up, down) => _ui.BeginInvoke(() => OnTraffic(up, down));

        foreach (var rec in _storage.LoadTunnels()) Tunnels.Add(new TunnelItem(rec));
        foreach (var app in _settings.Apps) Apps.Add(new AppItem(app));

        var selected = Tunnels.FirstOrDefault(t => t.Id == _settings.SelectedTunnelId) ?? Tunnels.FirstOrDefault();
        SelectTunnel(selected, save: false);
    }

    // ---------- События для окна ----------

    /// <summary>Новые строки журнала, пачкой (поток UI).</summary>
    public event Action<IReadOnlyList<string>>? LogAppended;

    /// <summary>Ошибка, которую нужно показать пользователю: заголовок, текст.</summary>
    public event Action<string, string>? ErrorRaised;

    /// <summary>Соединение оборвалось само — для уведомления в трее.</summary>
    public event Action<string>? ConnectionLost;

    // ---------- Туннели ----------

    public ObservableCollection<TunnelItem> Tunnels { get; } = new();

    public TunnelItem? SelectedTunnel => _selectedTunnel;
    public bool HasTunnels => Tunnels.Count > 0;
    public string SelectedTunnelName => _selectedTunnel?.Name ?? "Нет туннелей";
    public string SelectedTunnelDetail => _selectedTunnel == null ? "Импортируйте файл .conf" : _selectedEndpoint;
    public string? ConnectedTunnelName => _connectedTunnelName;

    public void SelectTunnel(TunnelItem? item, bool save = true)
    {
        foreach (var t in Tunnels) t.IsSelected = ReferenceEquals(t, item);
        _selectedTunnel = item;
        _selectedEndpoint = "";
        if (item != null)
        {
            try
            {
                var cfg = WgConfig.Parse(_storage.ReadTunnelConfig(item.Record));
                _selectedEndpoint = string.Join(", ", cfg.Peers.Select(p => p.EndpointDisplay));
            }
            catch (Exception ex)
            {
                _selectedEndpoint = ex is WgConfigException ? "ошибка в конфиге" : "не удалось прочитать конфиг";
            }
        }
        if (save)
        {
            _settings.SelectedTunnelId = item?.Id;
            SaveSettings();
        }
        OnPropertyChanged(nameof(SelectedTunnel));
        OnPropertyChanged(nameof(SelectedTunnelName));
        OnPropertyChanged(nameof(SelectedTunnelDetail));
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(CanDeleteTunnel));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(StatusDetail));
        UpdatePending();
    }

    /// <summary>Импорт .conf. Возвращает текст ошибки или null.</summary>
    public string? ImportTunnel(string name, string confText)
    {
        try
        {
            var cfg = WgConfig.Parse(confText);
            // Проверяем, что из конфига собирается рабочая схема маршрутизации.
            SingBoxConfigBuilder.Build(cfg, CurrentRouting(), new CoreParams { ApiPort = 1, ApiSecret = "x" });
        }
        catch (WgConfigException ex)
        {
            return ex.Message;
        }

        name = string.IsNullOrWhiteSpace(name) ? "Туннель" : name.Trim();
        string unique = name;
        for (int i = 2; Tunnels.Any(t => string.Equals(t.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
            unique = $"{name} ({i})";

        var rec = _storage.AddTunnel(unique, confText);
        var item = new TunnelItem(rec);
        Tunnels.Add(item);
        SelectTunnel(item);
        AppendLog($"[клиент] импортирован туннель «{unique}»");
        return null;
    }

    public bool CanDeleteTunnel => _selectedTunnel != null &&
        !(State != ConnState.Disconnected && _selectedTunnel.Name == _connectedTunnelName);

    public void DeleteSelectedTunnel()
    {
        var item = _selectedTunnel;
        if (item == null || !CanDeleteTunnel) return;
        _storage.DeleteTunnel(item.Id);
        int idx = Tunnels.IndexOf(item);
        Tunnels.Remove(item);
        SelectTunnel(Tunnels.Count == 0 ? null : Tunnels[Math.Min(idx, Tunnels.Count - 1)]);
        AppendLog($"[клиент] удалён туннель «{item.Name}»");
    }

    // ---------- Маршрутизация ----------

    public RoutingMode Mode
    {
        get => _settings.Mode;
        set
        {
            if (_settings.Mode == value) return;
            _settings.Mode = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModeAll));
            OnPropertyChanged(nameof(IsModeInclude));
            OnPropertyChanged(nameof(IsModeExclude));
            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(IsAppListActive));
            OnPropertyChanged(nameof(AppsHint));
            OnPropertyChanged(nameof(ModeWarning));
            UpdatePending();
        }
    }

    public bool IsModeAll { get => Mode == RoutingMode.All; set { if (value) Mode = RoutingMode.All; } }
    public bool IsModeInclude { get => Mode == RoutingMode.Include; set { if (value) Mode = RoutingMode.Include; } }
    public bool IsModeExclude { get => Mode == RoutingMode.Exclude; set { if (value) Mode = RoutingMode.Exclude; } }

    public string ModeDescription => Mode switch
    {
        RoutingMode.All => "Весь трафик компьютера идёт через туннель.",
        RoutingMode.Include => "Через туннель идут только приложения из списка, остальное — напрямую.",
        _ => "Через туннель идёт всё, кроме приложений из списка.",
    };

    /// <summary>Предупреждение, если выбранный режим без списка ничего не меняет.</summary>
    public string? ModeWarning => Mode != RoutingMode.All && Apps.Count == 0
        ? Mode == RoutingMode.Include
            ? "Список пуст — сейчас ни одно приложение не пойдёт через туннель."
            : "Список пуст — сейчас через туннель идёт весь трафик."
        : null;

    public bool LanBypass
    {
        get => _settings.LanBypass;
        set
        {
            if (_settings.LanBypass == value) return;
            _settings.LanBypass = value;
            SaveSettings();
            OnPropertyChanged();
            UpdatePending();
        }
    }

    // ---------- Приложения ----------

    public ObservableCollection<AppItem> Apps { get; } = new();
    public bool HasApps => Apps.Count > 0;
    public bool IsAppListActive => Mode != RoutingMode.All;
    public string AppsCount => Apps.Count == 0 ? "пусто" : Format.Plural(Apps.Count, "приложение", "приложения", "приложений");

    public string AppsHint => Mode switch
    {
        RoutingMode.Include => "Эти приложения пойдут через туннель",
        RoutingMode.Exclude => "Эти приложения пойдут напрямую, мимо туннеля",
        _ => "Список работает в режимах «Только выбранные» и «Кроме выбранных»",
    };

    public HashSet<string> AppNames => new(Apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Добавляет приложения по путям к exe. Возвращает число добавленных.</summary>
    public int AddApps(IEnumerable<string> exePaths)
    {
        int added = 0;
        foreach (string path in exePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string name = System.IO.Path.GetFileName(path);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (Apps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new AppEntry { Name = name, Path = path };
            _settings.Apps.Add(entry);
            Apps.Add(new AppItem(entry));
            added++;
        }
        if (added > 0) AppsChanged();
        return added;
    }

    public void RemoveApp(AppItem item)
    {
        _settings.Apps.Remove(item.Entry);
        Apps.Remove(item);
        AppsChanged();
    }

    private void AppsChanged()
    {
        SaveSettings();
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(AppsCount));
        OnPropertyChanged(nameof(ModeWarning));
        UpdatePending();
    }

    // ---------- Состояние подключения ----------

    public ConnState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateKey));
            OnPropertyChanged(nameof(StatusTitle));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanDeleteTunnel));
            OnPropertyChanged(nameof(ConnectedTunnelName));
            OnPropertyChanged(nameof(HasError));
            UpdatePending();
        }
    }

    public string StateKey => State.ToString();
    public bool IsConnected => State == ConnState.Connected;
    public bool IsBusy => State is ConnState.Connecting or ConnState.Disconnecting;
    public bool CanToggle => !IsBusy && (State == ConnState.Connected || _selectedTunnel != null);

    public string StatusTitle => State switch
    {
        ConnState.Connected => "Подключено",
        ConnState.Connecting => "Подключение…",
        ConnState.Disconnecting => "Отключение…",
        _ => "Не подключено",
    };

    public string StatusDetail => State switch
    {
        ConnState.Connected => Format.Duration(DateTime.UtcNow - _connectedAt),
        ConnState.Connecting => "Запуск ядра и туннеля",
        ConnState.Disconnecting => "Восстановление маршрутов",
        _ => _lastError ?? (_selectedTunnel == null ? "Импортируйте конфиг WireGuard" : "Нажмите, чтобы подключиться"),
    };

    public bool HasError => _lastError != null && State == ConnState.Disconnected;

    public string UpSpeed => Format.Speed(_upSpeed);
    public string DownSpeed => Format.Speed(_downSpeed);
    public string UpTotal => "всего " + Format.Size(_upTotal);
    public string DownTotal => "всего " + Format.Size(_downTotal);

    private bool _hasPendingChanges;
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => Set(ref _hasPendingChanges, value);
    }

    public async Task ToggleAsync()
    {
        if (State == ConnState.Connected) await DisconnectAsync();
        else if (State == ConnState.Disconnected) await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        if (State != ConnState.Disconnected) return;
        var tunnel = _selectedTunnel;
        if (tunnel == null)
        {
            SetError("Сначала импортируйте конфиг WireGuard (.conf).");
            return;
        }

        SetError(null);
        State = ConnState.Connecting;
        try
        {
            if (!Native.IsElevated())
                throw new CoreException("Клиент запущен без прав администратора — Windows не позволит создать сетевой адаптер. Перезапустите программу от имени администратора.");

            var cfg = WgConfig.Parse(_storage.ReadTunnelConfig(tunnel.Record));
            var routing = CurrentRouting();
            int port = FreeTcpPort();
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string json = SingBoxConfigBuilder.Build(cfg, routing, new CoreParams
            {
                ApiPort = port,
                ApiSecret = secret,
                // Если IPv6 в Windows отключён, адрес IPv6 на TUN назначить не получится.
                EnableIPv6 = Socket.OSSupportsIPv6,
            });

            AppendLog($"[клиент] подключение: «{tunnel.Name}», режим: {ModeName(routing.Mode)}, " +
                      $"приложений в списке: {routing.AppNames.Count}, локальная сеть напрямую: {(routing.LanBypass ? "да" : "нет")}");

            await _core.StartAsync(json);

            _appliedKey = CurrentKey();
            _connectedTunnelName = tunnel.Name;
            _connectedAt = DateTime.UtcNow;
            _upSpeed = _downSpeed = _upTotal = _downTotal = 0;
            NotifyTraffic();
            _traffic.Start(port, secret);
            _clock.Start();
            State = ConnState.Connected;
            AppendLog("[клиент] подключено");
        }
        catch (Exception ex)
        {
            // Любая ошибка (в т.ч. неожиданная) должна вернуть кнопку в рабочее состояние.
            _traffic.Stop();
            _clock.Stop();
            if (_core.IsRunning) await _core.StopAsync();
            _connectedTunnelName = null;
            State = ConnState.Disconnected;
            string title = ex is WgConfigException ? "Ошибка в конфиге" : "Не удалось подключиться";
            SetError(title);
            AppendLog($"[клиент] {title}: {ex.Message}");
            ErrorRaised?.Invoke(title, ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        if (State != ConnState.Connected) return;
        State = ConnState.Disconnecting;
        _traffic.Stop();
        _clock.Stop();
        try
        {
            await _core.StopAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"[клиент] ошибка при остановке ядра: {ex.Message}");
            _core.KillNow();
        }
        finally
        {
            ResetTraffic();
            _connectedTunnelName = null;
            State = ConnState.Disconnected;
        }
        AppendLog("[клиент] отключено");
    }

    /// <summary>Переподключение с текущими настройками.</summary>
    public async Task ApplyChangesAsync()
    {
        if (State != ConnState.Connected) return;
        await DisconnectAsync();
        await ConnectAsync();
    }

    /// <summary>Остановка перед выходом из программы.</summary>
    public async Task ShutdownAsync()
    {
        _traffic.Stop();
        _clock.Stop();
        await _core.StopAsync();
    }

    private void OnCoreCrashed(string reason)
    {
        if (State is not (ConnState.Connected or ConnState.Connecting)) return;
        _traffic.Stop();
        _clock.Stop();
        ResetTraffic();
        _connectedTunnelName = null;
        State = ConnState.Disconnected;
        SetError("Соединение прервано");
        AppendLog($"[клиент] соединение прервано: {reason}");
        ConnectionLost?.Invoke(reason);
    }

    private void OnTraffic(long up, long down)
    {
        if (State != ConnState.Connected) return;
        _upSpeed = up;
        _downSpeed = down;
        _upTotal += up;
        _downTotal += down;
        NotifyTraffic();
    }

    private void ResetTraffic()
    {
        _upSpeed = _downSpeed = 0;
        NotifyTraffic();
    }

    private void NotifyTraffic()
    {
        OnPropertyChanged(nameof(UpSpeed));
        OnPropertyChanged(nameof(DownSpeed));
        OnPropertyChanged(nameof(UpTotal));
        OnPropertyChanged(nameof(DownTotal));
    }

    private void SetError(string? message)
    {
        _lastError = message;
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(HasError));
    }

    // ---------- Отложенные изменения ----------

    private RoutingOptions CurrentRouting() => new()
    {
        Mode = Mode,
        LanBypass = LanBypass,
        AppNames = Apps.Select(a => a.Name).ToList(),
    };

    private string CurrentKey() =>
        $"{_selectedTunnel?.Id}|{Mode}|{LanBypass}|" +
        string.Join("/", Apps.Select(a => a.Name.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));

    private void UpdatePending() =>
        HasPendingChanges = State == ConnState.Connected && _appliedKey != null && _appliedKey != CurrentKey();

    // ---------- Журнал ----------

    /// <summary>
    /// Весь журнал для окна, которое только что открылось. Очередь неотправленных строк сбрасывается
    /// под той же блокировкой — иначе они пришли бы в окно второй раз со следующей пачкой.
    /// </summary>
    public IReadOnlyList<string> TakeLogSnapshot()
    {
        lock (_log)
        {
            _pendingLog.Clear();
            return _log.ToList();
        }
    }

    public void ClearLog()
    {
        lock (_log)
        {
            _log.Clear();
            _pendingLog.Clear();
        }
    }

    /// <summary>Показывать в журнале каждое соединение и DNS-запрос. Не сохраняется между запусками.</summary>
    public bool VerboseLog
    {
        get => _core.Verbose;
        set
        {
            if (_core.Verbose == value) return;
            _core.Verbose = value;
            OnPropertyChanged();
            AppendLog(value
                ? "[клиент] подробный журнал включён: видны все соединения и DNS-запросы"
                : "[клиент] подробный журнал выключен");
        }
    }

    private void AppendLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        lock (_log)
        {
            _log.Add(stamped);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            _pendingLog.Add(stamped);
            // Если окно журнала долго не забирает строки, хвост всё равно есть в _log.
            if (_pendingLog.Count > MaxLogLines) _pendingLog.RemoveRange(0, _pendingLog.Count - MaxLogLines);
        }
    }

    private void FlushLog()
    {
        List<string> batch;
        lock (_log)
        {
            if (_pendingLog.Count == 0) return;
            batch = new List<string>(_pendingLog);
            _pendingLog.Clear();
        }
        LogAppended?.Invoke(batch);
    }

    // ---------- Прочее ----------

    public bool TrayHintShown
    {
        get => _settings.TrayHintShown;
        set
        {
            _settings.TrayHintShown = value;
            SaveSettings();
        }
    }

    public string DataDirectory => _storage.BaseDir;

    private void SaveSettings()
    {
        try
        {
            _storage.SaveSettings(_settings);
        }
        catch (IOException ex)
        {
            AppendLog($"[клиент] не удалось сохранить настройки: {ex.Message}");
        }
    }

    private static string ModeName(RoutingMode mode) => mode switch
    {
        RoutingMode.All => "весь трафик",
        RoutingMode.Include => "только выбранные",
        _ => "кроме выбранных",
    };

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _clock.Stop();
        _logFlush.Stop();
        _traffic.Dispose();
        _core.Dispose();
    }
}
