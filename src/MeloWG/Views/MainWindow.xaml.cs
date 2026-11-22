using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloWG.Core;
using MeloWG.ViewModels;
using Microsoft.Win32;

namespace MeloWG.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private LogWindow? _logWindow;
    private bool _allowClose;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.ErrorRaised += (title, message) => Dialogs.Error(this, title, message);

        StateChanged += (_, _) =>
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    /// <summary>Окно закрыли крестиком — оно уходит в трей.</summary>
    public event Action? HiddenToTray;

    public void AllowClose() => _allowClose = true;

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    // ---------- Заголовок ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is { IsLoaded: true })
        {
            if (_logWindow.WindowState == WindowState.Minimized) _logWindow.WindowState = WindowState.Normal;
            _logWindow.Activate();
            return;
        }
        _logWindow = new LogWindow(_vm) { Owner = this };
        _logWindow.Show();
    }

    // ---------- Подключение ----------

    private async void Power_Click(object sender, RoutedEventArgs e) => await _vm.ToggleAsync();

    private async void Apply_Click(object sender, RoutedEventArgs e) => await _vm.ApplyChangesAsync();

    // ---------- Туннели ----------

    private void TunnelItem_Click(object sender, RoutedEventArgs e)
    {
        TunnelToggle.IsChecked = false;
        if (sender is FrameworkElement { Tag: TunnelItem item }) _vm.SelectTunnel(item);
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        TunnelToggle.IsChecked = false;
        var dlg = new OpenFileDialog
        {
            Title = "Импорт туннеля WireGuard",
            Filter = "Конфигурация WireGuard (*.conf)|*.conf|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) ImportFiles(dlg.FileNames);
    }

    private void ImportClipboard_Click(object sender, RoutedEventArgs e)
    {
        TunnelToggle.IsChecked = false;
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { text = ""; }

        if (!WgConfig.LooksLikeConfig(text))
        {
            Dialogs.Error(this, "В буфере обмена нет конфига",
                "Скопируйте содержимое файла .conf целиком — от строки [Interface] до конца секции [Peer] — и попробуйте снова.");
            return;
        }

        string? name = Dialogs.Prompt(this, "Новый туннель", "Название туннеля", $"Туннель {_vm.Tunnels.Count + 1}");
        if (name == null) return;
        string? error = _vm.ImportTunnel(name, text);
        if (error != null) Dialogs.Error(this, "Конфиг не импортирован", error);
    }

    private void DeleteTunnel_Click(object sender, RoutedEventArgs e)
    {
        TunnelToggle.IsChecked = false;
        var t = _vm.SelectedTunnel;
        if (t == null) return;
        if (Dialogs.Confirm(this, "Удалить туннель?",
                $"Туннель «{t.Name}» будет удалён из клиента. Исходный файл .conf, если он у вас есть, останется на месте.",
                "Удалить", danger: true))
            _vm.DeleteSelectedTunnel();
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string text;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 64 * 1024) throw new IOException("Файл слишком большой для конфига WireGuard.");
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Dialogs.Error(this, $"Не удалось прочитать «{name}»", ex.Message);
                continue;
            }
            string? error = _vm.ImportTunnel(name, text);
            if (error != null) Dialogs.Error(this, $"Конфиг «{name}» не импортирован", error);
        }
    }

    // ---------- Приложения ----------

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите программы",
            Filter = "Программы (*.exe)|*.exe",
            Multiselect = true,
            DereferenceLinks = true,
        };
        if (dlg.ShowDialog(this) == true) _vm.AddApps(dlg.FileNames);
    }

    private void AddRunning_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow(_vm.AppNames) { Owner = this };
        if (picker.ShowDialog() == true) _vm.AddApps(picker.SelectedPaths);
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppItem item }) _vm.RemoveApp(item);
    }

    // ---------- Перетаскивание файлов ----------

    private static readonly string[] AcceptedExt = { ".conf", ".exe", ".lnk" };

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => AcceptedExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = DroppedFiles(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var files = DroppedFiles(e);
        if (files.Length == 0) return;

        var confs = files.Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (confs.Count > 0) ImportFiles(confs);

        var exes = files
            .Where(f => !f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(f) : f)
            .Where(f => f != null && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToList();
        if (exes.Count > 0) _vm.AddApps(exes);
        else if (confs.Count == 0)
            Dialogs.Error(this, "Ярлык не ведёт к программе", "Перетащите ярлык программы (.exe) или сам исполняемый файл.");
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }
}
