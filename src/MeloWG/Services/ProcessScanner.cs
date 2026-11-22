using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MeloWG.Core;

namespace MeloWG.Services;

public sealed record RunningApp(string Name, string Path, bool HasWindow, bool IsSystem);

public static class ProcessScanner
{
    // Служебные процессы Windows с невидимыми окнами — в списке они только мешают.
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "SearchUI.exe", "LockApp.exe", "ctfmon.exe", "sihost.exe", "taskhostw.exe",
        "RuntimeBroker.exe", "conhost.exe", "dwm.exe", "dllhost.exe", "SystemSettingsBroker.exe", "smartscreen.exe",
    };

    /// <summary>Запущенные программы без дублей: сначала с окнами, затем по имени.</summary>
    public static List<RunningApp> Scan()
    {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        string ownDir = AppContext.BaseDirectory;
        var result = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                string? path = Native.GetProcessPath(p.Id);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.StartsWith(ownDir, StringComparison.OrdinalIgnoreCase)) continue; // сам клиент и его ядро

                string fileName = System.IO.Path.GetFileName(path);
                bool shellHost = ShellHosts.Contains(fileName);
                bool hasWindow = false;
                try { hasWindow = !shellHost && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle); } catch { }

                if (result.TryGetValue(path, out var existing))
                {
                    if (hasWindow && !existing.HasWindow) result[path] = existing with { HasWindow = true };
                    continue;
                }

                bool isSystem = shellHost || path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
                result[path] = new RunningApp(fileName, path, hasWindow, isSystem);
            }
        }

        return result.Values
            .OrderByDescending(a => a.HasWindow)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
