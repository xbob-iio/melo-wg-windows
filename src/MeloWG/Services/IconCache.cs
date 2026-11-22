using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeloWG.Services;

/// <summary>Иконки exe-файлов для списков. Потокобезопасно, результаты заморожены.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();
    private static ImageSource? _fallback;

    public static ImageSource Get(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            lock (Sync)
                if (Cache.TryGetValue(path, out var cached)) return cached;

            ImageSource? img = TryExtract(path);
            if (img != null)
            {
                lock (Sync) Cache[path] = img;
                return img;
            }
        }
        return Fallback;
    }

    private static ImageSource Fallback
    {
        get
        {
            if (_fallback != null) return _fallback;
            var src = FromIcon(System.Drawing.SystemIcons.Application);
            _fallback = src;
            return src;
        }
    }

    private static ImageSource? TryExtract(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? null : FromIcon(icon);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource FromIcon(System.Drawing.Icon icon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
