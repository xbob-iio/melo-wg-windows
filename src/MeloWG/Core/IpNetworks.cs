using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace MeloWG.Core;

/// <summary>Префикс сети вида 10.0.0.2/24 (адрес хранится как есть, без обнуления хостовых битов).</summary>
public readonly record struct IpPrefix(IPAddress Address, int Length)
{
    public bool IsV6 => Address.AddressFamily == AddressFamily.InterNetworkV6;
    public int Family => IsV6 ? 6 : 4;
    public int Width => IsV6 ? 128 : 32;

    /// <summary>Префикс с обнулёнными хостовыми битами: 10.0.0.2/24 → 10.0.0.0/24.</summary>
    public IpPrefix Network => new(IpMath.FromNumber(IpMath.ToNumber(Address) & IpMath.Mask(Length, IsV6), IsV6), Length);

    public static bool TryParse(string? text, out IpPrefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        string addrPart = text;
        int? length = null;
        int slash = text.IndexOf('/');
        if (slash >= 0)
        {
            addrPart = text[..slash];
            if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int len)) return false;
            length = len;
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork && addr.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // IPAddress.TryParse принимает «1» как 0.0.0.1 — такие сокращения в конфиге WireGuard недопустимы.
        if (addr.AddressFamily == AddressFamily.InterNetwork && addrPart.Count(c => c == '.') != 3) return false;

        addr = IpMath.StripScope(addr);
        int width = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        int l = length ?? width;
        if (l < 0 || l > width) return false;
        prefix = new IpPrefix(addr, l);
        return true;
    }

    public static IpPrefix Parse(string text) =>
        TryParse(text, out var p) ? p : throw new FormatException($"Некорректный адрес или подсеть: {text}");

    public override string ToString() => $"{Address}/{Length}";
}

/// <summary>Множество IP-адресов одного или обоих семейств как набор непересекающихся диапазонов.</summary>
public sealed class IpSet
{
    private readonly List<(UInt128 Start, UInt128 End)> _v4;
    private readonly List<(UInt128 Start, UInt128 End)> _v6;

    private IpSet(List<(UInt128, UInt128)> v4, List<(UInt128, UInt128)> v6)
    {
        _v4 = Normalize(v4);
        _v6 = Normalize(v6);
    }

    public static IpSet Empty { get; } = new(new(), new());

    public static IpSet From(IEnumerable<IpPrefix> prefixes)
    {
        var v4 = new List<(UInt128, UInt128)>();
        var v6 = new List<(UInt128, UInt128)>();
        foreach (var p in prefixes)
            (p.IsV6 ? v6 : v4).Add(IpMath.RangeOf(p));
        return new IpSet(v4, v6);
    }

    public static IpSet From(params string[] prefixes) => From(prefixes.Select(IpPrefix.Parse));

    public bool IsEmpty => _v4.Count == 0 && _v6.Count == 0;
    public bool HasFamily(int family) => (family == 6 ? _v6 : _v4).Count > 0;

    public bool CoversAll(int family)
    {
        var list = family == 6 ? _v6 : _v4;
        return list.Count == 1 && list[0].Start == UInt128.Zero && list[0].End == IpMath.MaxValue(family == 6);
    }

    public bool Contains(IPAddress address)
    {
        address = IpMath.StripScope(address);
        bool v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        UInt128 n = IpMath.ToNumber(address);
        foreach (var (s, e) in v6 ? _v6 : _v4)
            if (n >= s && n <= e) return true;
        return false;
    }

    /// <summary>Весь префикс целиком лежит внутри множества.</summary>
    public bool ContainsPrefix(IpPrefix prefix)
    {
        var (ps, pe) = IpMath.RangeOf(prefix);
        foreach (var (s, e) in prefix.IsV6 ? _v6 : _v4)
            if (ps >= s && pe <= e) return true;
        return false;
    }

    public IpSet Union(IpSet other) =>
        new(_v4.Concat(other._v4).ToList(), _v6.Concat(other._v6).ToList());

    public IpSet Subtract(IpSet other) =>
        new(SubtractRanges(_v4, other._v4), SubtractRanges(_v6, other._v6));

    /// <summary>Минимальный набор CIDR-префиксов, точно покрывающий множество.</summary>
    public List<IpPrefix> ToPrefixes(int? family = null)
    {
        var result = new List<IpPrefix>();
        if (family is null or 4)
            foreach (var (s, e) in _v4) result.AddRange(IpMath.Decompose(s, e, false));
        if (family is null or 6)
            foreach (var (s, e) in _v6) result.AddRange(IpMath.Decompose(s, e, true));
        return result;
    }

    private static List<(UInt128 Start, UInt128 End)> Normalize(List<(UInt128 Start, UInt128 End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(UInt128 Start, UInt128 End)>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                // Сливаем пересекающиеся и соседние диапазоны (last.End + 1 без переполнения).
                if (r.Start <= last.End || (last.End != UInt128.MaxValue && r.Start == last.End + 1))
                {
                    if (r.End > last.End) merged[^1] = (last.Start, r.End);
                    continue;
                }
            }
            merged.Add(r);
        }
        return merged;
    }

    private static List<(UInt128, UInt128)> SubtractRanges(List<(UInt128 Start, UInt128 End)> a, List<(UInt128 Start, UInt128 End)> b)
    {
        var result = new List<(UInt128, UInt128)>();
        foreach (var (start, end) in a)
        {
            UInt128 cur = start;
            bool exhausted = false;
            foreach (var (bs, be) in b)
            {
                if (be < cur || bs > end) continue;
                if (bs > cur) result.Add((cur, bs - 1));
                if (be >= end) { exhausted = true; break; }
                cur = be + 1;
            }
            if (!exhausted && cur <= end) result.Add((cur, end));
        }
        return result;
    }
}

internal static class IpMath
{
    public static UInt128 MaxValue(bool v6) => v6 ? UInt128.MaxValue : (UInt128)uint.MaxValue;

    public static IPAddress StripScope(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;

    public static UInt128 ToNumber(IPAddress address)
    {
        UInt128 n = UInt128.Zero;
        foreach (byte b in address.GetAddressBytes()) n = (n << 8) | b;
        return n;
    }

    public static IPAddress FromNumber(UInt128 n, bool v6)
    {
        int len = v6 ? 16 : 4;
        var bytes = new byte[len];
        for (int i = len - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(n & 0xFF);
            n >>= 8;
        }
        return new IPAddress(bytes);
    }

    public static UInt128 HostMask(int hostBits) =>
        hostBits <= 0 ? UInt128.Zero : hostBits >= 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - UInt128.One;

    public static UInt128 Mask(int length, bool v6)
    {
        int width = v6 ? 128 : 32;
        return MaxValue(v6) ^ HostMask(width - length);
    }

    public static (UInt128 Start, UInt128 End) RangeOf(IpPrefix p)
    {
        UInt128 start = ToNumber(p.Address) & Mask(p.Length, p.IsV6);
        return (start, start | HostMask(p.Width - p.Length));
    }

    public static IEnumerable<IpPrefix> Decompose(UInt128 start, UInt128 end, bool v6)
    {
        int width = v6 ? 128 : 32;
        while (true)
        {
            int hostBits = start == UInt128.Zero ? width : Math.Min(width, (int)UInt128.TrailingZeroCount(start));
            UInt128 blockEnd;
            while (true)
            {
                blockEnd = start + HostMask(hostBits);
                if (blockEnd >= start && blockEnd <= end) break;
                hostBits--;
            }
            yield return new IpPrefix(FromNumber(start, v6), width - hostBits);
            if (blockEnd >= end) yield break;
            start = blockEnd + 1;
        }
    }
}
