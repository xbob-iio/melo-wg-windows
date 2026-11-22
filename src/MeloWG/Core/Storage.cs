using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeloWG.Core;

public sealed class AppEntry
{
    /// <summary>Имя исполняемого файла, по которому идёт сопоставление (chrome.exe).</summary>
    public string Name { get; set; } = "";
    /// <summary>Путь, откуда приложение добавлено (для иконки и подсказки).</summary>
    public string? Path { get; set; }
}

public sealed class AppSettings
{
    public RoutingMode Mode { get; set; } = RoutingMode.All;
    public bool LanBypass { get; set; } = true;
    public List<AppEntry> Apps { get; set; } = new();
    public string? SelectedTunnelId { get; set; }
    public bool TrayHintShown { get; set; }
}

public sealed class TunnelRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Added { get; set; }
    /// <summary>Текст .conf, зашифрованный DPAPI (ключ текущего пользователя), base64.</summary>
    public string Data { get; set; } = "";
}

/// <summary>Хранение настроек и туннелей в %APPDATA%\MeloWG.</summary>
public sealed class Storage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Storage(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeloWG");
        Directory.CreateDirectory(TunnelsDir);
    }

    public string BaseDir { get; }
    public string TunnelsDir => Path.Combine(BaseDir, "tunnels");
    public string CoreDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeloWG", "core");
    private string SettingsPath => Path.Combine(BaseDir, "settings.json");

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch
        {
            // Повреждённый файл настроек не должен мешать запуску — начинаем с настроек по умолчанию.
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings) => WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));

    public List<TunnelRecord> LoadTunnels()
    {
        var list = new List<TunnelRecord>();
        foreach (string file in Directory.EnumerateFiles(TunnelsDir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<TunnelRecord>(File.ReadAllText(file), Json);
                if (rec != null && rec.Id.Length > 0) list.Add(rec);
            }
            catch
            {
                // Пропускаем повреждённый файл.
            }
        }
        return list.OrderBy(t => t.Added).ToList();
    }

    public TunnelRecord AddTunnel(string name, string confText)
    {
        var rec = new TunnelRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Added = DateTime.UtcNow,
            Data = Convert.ToBase64String(Native.Protect(Encoding.UTF8.GetBytes(confText))),
        };
        WriteAtomic(TunnelPath(rec.Id), JsonSerializer.Serialize(rec, Json));
        return rec;
    }

    public string ReadTunnelConfig(TunnelRecord rec) =>
        Encoding.UTF8.GetString(Native.Unprotect(Convert.FromBase64String(rec.Data)));

    public void DeleteTunnel(string id)
    {
        string path = TunnelPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private string TunnelPath(string id)
    {
        if (id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Некорректный идентификатор туннеля.");
        return Path.Combine(TunnelsDir, id + ".json");
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
