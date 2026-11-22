using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MeloWG.Core;
using MeloWG.Services;
using MeloWG.ViewModels;
using MeloWG.Views;

namespace MeloWG;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private CoreRunner? _core;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        int previewIdx = Array.IndexOf(e.Args, "--render-preview");
        if (previewIdx >= 0 && previewIdx + 1 < e.Args.Length)
        {
            _ = PreviewRenderer.RunAsync(this, e.Args[previewIdx + 1]);
            return;
        }
#endif

        // Одна копия: повторный запуск просто показывает уже открытое окно.
        _mutex = new Mutex(true, @"Local\MeloWG.SingleInstance", out _ownsMutex);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MeloWG.Activate");
        if (!_ownsMutex)
        {
            _activateEvent.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_activateEvent.WaitOne()) Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
        })
        { IsBackground = true, Name = "MeloWG.Activate" };
        listener.Start();

        DispatcherUnhandledException += OnUnhandledException;

        var storage = new Storage();
        _core = new CoreRunner(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), storage.CoreDataDir);
        _vm = new MainViewModel(storage, _core, new TrafficMonitor(), Dispatcher);
        _window = new MainWindow(_vm);
        _tray = new TrayIcon();

        _tray.OpenRequested += () => _window.ShowFromTray();
        _tray.ToggleRequested += async () => await _vm.ToggleAsync();
        _tray.ExitRequested += async () => await ExitAsync();
        _vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(MainViewModel.State)) _tray.Update(_vm.State, _vm.ConnectedTunnelName);
        };
        _vm.ConnectionLost += reason => _tray.ShowBalloon("Melo WG: соединение прервано", reason, error: true);
        _window.HiddenToTray += () =>
        {
            if (_vm.TrayHintShown) return;
            _tray.ShowBalloon("Melo WG работает в фоне", "Щелчок по значку открывает окно, правая кнопка — меню и выход.");
            _vm.TrayHintShown = true;
        };
        _tray.Update(_vm.State, null);

        SessionEnding += (_, _) => _core.KillNow();

        if (!e.Args.Contains("--minimized")) _window.Show();
    }

    /// <summary>Полный выход: остановить туннель, убрать значок, закрыть окна.</summary>
    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_vm != null) await _vm.ShutdownAsync();
        }
        catch
        {
            _core?.KillNow();
        }
        _tray?.Dispose();
        _tray = null;
        _window?.AllowClose();
        _window?.Close();
        _vm?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _core?.KillNow();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Error(_window, "Непредвиденная ошибка", e.Exception.Message);
    }
}
