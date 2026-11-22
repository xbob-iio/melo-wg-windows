#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloWG.Core;
using MeloWG.ViewModels;
using MeloWG.Views;

namespace MeloWG.Services;

/// <summary>
/// Отладочный режим: рисует окна в PNG без запуска ядра и без изменения настроек пользователя.
/// MeloWG.exe --render-preview &lt;папка&gt;
/// </summary>
internal static class PreviewRenderer
{
    private const string SampleConf = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.66.66.2/32
        DNS = 1.1.1.1

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        Endpoint = nl-ams.example.net:51820
        AllowedIPs = 0.0.0.0/0, ::/0
        PersistentKeepalive = 25
        """;

    public static async Task RunAsync(Application app, string outDir)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "melowg-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outDir);
            var storage = new Storage(dataDir);
            using var core = new CoreRunner("nonexistent.exe", Path.Combine(dataDir, "core"));
            var vm = new MainViewModel(storage, core, new TrafficMonitor(), app.Dispatcher);

            var main = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false };
            main.Show();
            await Settle();
            Save(main, Path.Combine(outDir, "01-empty.png"));

            vm.ImportTunnel("Нидерланды", SampleConf);
            vm.ImportTunnel("Офис", SampleConf.Replace("nl-ams.example.net", "vpn.office.example.com"));
            vm.SelectTunnel(vm.Tunnels.First());
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            vm.AddApps(new[]
            {
                Path.Combine(win, "explorer.exe"),
                Path.Combine(sys, "notepad.exe"),
                Path.Combine(sys, "mspaint.exe"),
                Path.Combine(sys, "cmd.exe"),
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            });
            vm.Mode = RoutingMode.Include;
            await Settle();
            Save(main, Path.Combine(outDir, "02-include-disconnected.png"));

            vm.PreviewConnected(TimeSpan.FromMinutes(42).Add(TimeSpan.FromSeconds(17)), 312_000, 4_870_000, 48_200_000, 1_730_000_000, pending: false);
            await Settle(900);
            Save(main, Path.Combine(outDir, "03-connected.png"));

            vm.Mode = RoutingMode.Exclude;
            await Settle();
            Save(main, Path.Combine(outDir, "04-connected-pending.png"));

            var dlg = new DialogWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false };
            dlg.TitleText.Text = "Удалить туннель?";
            dlg.MessageText.Text = "Туннель «Офис» будет удалён из клиента. Исходный файл .conf, если он у вас есть, останется на месте.";
            dlg.PrimaryButton.Content = "Удалить";
            dlg.PrimaryButton.Style = (Style)dlg.FindResource("Btn.Danger");
            dlg.SecondaryButton.Content = "Отмена";
            dlg.IconBadge.Background = (Brush)dlg.FindResource("DangerSoft");
            dlg.IconGlyph.Foreground = (Brush)dlg.FindResource("Danger");
            dlg.IconGlyph.Text = "\uE74D";
            dlg.Show();
            await Settle();
            Save(dlg, Path.Combine(outDir, "05-dialog.png"));
            dlg.Close();

            var picker = new ProcessPickerWindow(vm.AppNames) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false };
            picker.Show();
            await Settle(2500);
            picker.ProcList.SelectedIndex = 1;
            await Settle(300);
            Save(picker, Path.Combine(outDir, "06-picker.png"));
            picker.Close();

            foreach (string l in new[]
                     {
                         "[клиент] подключение: «Нидерланды», режим: кроме выбранных, приложений в списке: 5, локальная сеть напрямую: да",
                         "INFO[0001] inbound/tun[tun-in]: started at MeloWG",
                         "INFO[0001] network: updated default interface Ethernet, index 16",
                         "INFO[0001] clash-api: restful api listening at 127.0.0.1:54380",
                         "INFO[0001] sing-box started (1.71s)",
                         "[клиент] подключено",
                     })
                vm.PreviewLog(l);
            var log = new LogWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false };
            log.Show();
            await Settle(500);
            Save(log, Path.Combine(outDir, "07-log.png"));
            log.Close();

            main.AllowClose();
            main.Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch { }
            app.Shutdown();
        }
    }

    private static async Task Settle(int ms = 600)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window w, string path)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        int width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(w.Background, null, rect);
            dc.DrawRectangle(new VisualBrush(root), null, rect);
        }

        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
