using System;
using System.Globalization;

namespace MeloWG.Services;

public static class Format
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

    public static string Size(long bytes)
    {
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        string num = i == 0 || v >= 100 ? v.ToString("0", Ru) : v >= 10 ? v.ToString("0.0", Ru) : v.ToString("0.00", Ru);
        return $"{num} {Units[i]}";
    }

    public static string Speed(long bytesPerSecond) => Size(bytesPerSecond) + "/с";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Русское склонение: 1 приложение, 2 приложения, 5 приложений.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int m100 = Math.Abs(n) % 100, m10 = m100 % 10;
        string word = m100 is >= 11 and <= 14 ? many : m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }
}
