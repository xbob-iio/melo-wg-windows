using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MeloWG.ViewModels;
using WinForms = System.Windows.Forms;

namespace MeloWG.Services;

/// <summary>Значок в области уведомлений: состояние подключения и быстрые действия.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly Icon _idle, _busy, _connected;

    public event Action? OpenRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        int size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        _idle = Render(size, Color.FromArgb(0x8D, 0x97, 0xA8), dimmed: true);
        _busy = Render(size, Color.FromArgb(0xF5, 0xB5, 0x44), dimmed: false);
        _connected = Render(size, Color.FromArgb(0x2B, 0xD4, 0x7D), dimmed: false);

        var menu = new WinForms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        var open = new WinForms.ToolStripMenuItem("Открыть Melo WG") { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        _toggleItem = new WinForms.ToolStripMenuItem("Подключить");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();
        var exit = new WinForms.ToolStripMenuItem("Выход");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange(new WinForms.ToolStripItem[] { open, _toggleItem, new WinForms.ToolStripSeparator(), exit });

        _icon = new WinForms.NotifyIcon
        {
            Icon = _idle,
            Text = "Melo WG — не подключено",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public void Update(ConnState state, string? tunnelName)
    {
        _icon.Icon = state switch
        {
            ConnState.Connected => _connected,
            ConnState.Connecting or ConnState.Disconnecting => _busy,
            _ => _idle,
        };
        string status = state switch
        {
            ConnState.Connected => "подключено" + (tunnelName != null ? $": {tunnelName}" : ""),
            ConnState.Connecting => "подключение…",
            ConnState.Disconnecting => "отключение…",
            _ => "не подключено",
        };
        string text = "Melo WG — " + status;
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text;
        _toggleItem.Text = state == ConnState.Connected ? "Отключить" : "Подключить";
        _toggleItem.Enabled = state is ConnState.Connected or ConnState.Disconnected;
    }

    public void ShowBalloon(string title, string text, bool error = false) =>
        _icon.ShowBalloonTip(5000, title, text, error ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _idle.Dispose();
        _busy.Dispose();
        _connected.Dispose();
    }

    // Рисуем значок кодом (тот же мотив, что и app.ico) + точка состояния.
    private static Icon Render(int s, Color dot, bool dimmed)
    {
        using var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = s / 100f;

            using (var bg = RoundedRect(3 * k, 3 * k, 94 * k, 94 * k, 24 * k))
            using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                       dimmed ? Color.FromArgb(0x4A, 0x55, 0x68) : Color.FromArgb(0x3B, 0x6E, 0xF6),
                       dimmed ? Color.FromArgb(0x35, 0x3E, 0x4E) : Color.FromArgb(0x12, 0xB8, 0xD6)))
                g.FillPath(grad, bg);

            using (var shield = new GraphicsPath())
            {
                shield.AddLine(50 * k, 17 * k, 77 * k, 27 * k);
                shield.AddLine(77 * k, 27 * k, 77 * k, 47 * k);
                shield.AddBezier(77 * k, 47 * k, 77 * k, 67 * k, 65 * k, 79 * k, 50 * k, 85 * k);
                shield.AddBezier(50 * k, 85 * k, 35 * k, 79 * k, 23 * k, 67 * k, 23 * k, 47 * k);
                shield.AddLine(23 * k, 47 * k, 23 * k, 27 * k);
                shield.CloseFigure();
                g.FillPath(Brushes.White, shield);
            }

            float d = s * 0.46f;
            float x = s - d - 0.5f, y = s - d - 0.5f;
            using (var outline = new SolidBrush(Color.FromArgb(0x0D, 0x11, 0x17)))
                g.FillEllipse(outline, x - s * 0.06f, y - s * 0.06f, d + s * 0.12f, d + s * 0.12f);
            using (var fill = new SolidBrush(dot))
                g.FillEllipse(fill, x, y, d, d);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(0x1B, 0x21, 0x2B);
        private static readonly Color Hover = Color.FromArgb(0x2A, 0x32, 0x40);
        private static readonly Color Line = Color.FromArgb(0x33, 0x3C, 0x4B);
        private static readonly Color Fore = Color.FromArgb(0xE8, 0xEC, 0xF2);
        private static readonly Color ForeDisabled = Color.FromArgb(0x5E, 0x68, 0x78);

        public DarkMenuRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fore : ForeDisabled;
            base.OnRenderItemText(e);
        }

        private sealed class Colors : WinForms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
        }
    }
}
