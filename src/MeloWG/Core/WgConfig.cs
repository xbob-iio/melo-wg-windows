using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;

namespace MeloWG.Core;

public sealed class WgConfigException : Exception
{
    public WgConfigException(string message) : base(message) { }
}

public sealed class WgPeer
{
    public string PublicKey { get; set; } = "";
    public string? PresharedKey { get; set; }
    public string EndpointHost { get; set; } = "";
    public int EndpointPort { get; set; }
    public List<IpPrefix> AllowedIps { get; } = new();
    public int? PersistentKeepalive { get; set; }

    public string EndpointDisplay => EndpointHost.Contains(':') ? $"[{EndpointHost}]:{EndpointPort}" : $"{EndpointHost}:{EndpointPort}";
}

/// <summary>Конфигурация WireGuard в формате wg-quick (.conf).</summary>
public sealed class WgConfig
{
    public string PrivateKey { get; private set; } = "";
    public List<IpPrefix> Addresses { get; } = new();
    public List<IPAddress> Dns { get; } = new();
    public int? Mtu { get; private set; }
    public int? ListenPort { get; private set; }
    public List<WgPeer> Peers { get; } = new();

    // Параметры обфускации AmneziaWG: в обычном WireGuard их нет, и sing-box их не поддерживает.
    private static readonly HashSet<string> AmneziaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5", "j1", "j2", "j3", "itime",
    };

    // Ключи wg-quick, которые выполняют команды или управляют таблицами маршрутов — к клиенту не относятся.
    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "postup", "postdown", "preup", "predown", "table", "saveconfig", "fwmark",
    };

    /// <summary>Быстрая проверка, похож ли текст на конфиг WireGuard.</summary>
    public static bool LooksLikeConfig(string? text) =>
        text != null && text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    public static WgConfig Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new WgConfigException("Конфигурация пуста.");

        var cfg = new WgConfig();
        bool hasInterface = false;
        WgPeer? peer = null;
        string section = "";

        string[] lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = lines[i];
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section == "interface")
                {
                    if (hasInterface) throw Err(lineNo, "секция [Interface] указана дважды.");
                    hasInterface = true;
                }
                else if (section == "peer")
                {
                    peer = new WgPeer();
                    cfg.Peers.Add(peer);
                }
                else throw Err(lineNo, $"неизвестная секция [{line[1..^1]}].");
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) throw Err(lineNo, "ожидается строка вида «Ключ = Значение».");
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();

            if (AmneziaKeys.Contains(key))
                throw new WgConfigException(
                    "Это конфигурация AmneziaWG (есть параметры обфускации Jc/H1/S1…). " +
                    "Клиент работает только с обычным WireGuard: запросите у провайдера стандартный WireGuard-конфиг.");
            if (IgnoredKeys.Contains(key)) continue;

            switch (section)
            {
                case "interface":
                    ParseInterfaceKey(cfg, key, value, lineNo);
                    break;
                case "peer":
                    ParsePeerKey(peer!, key, value, lineNo);
                    break;
                default:
                    throw Err(lineNo, "параметр вне секции [Interface] или [Peer].");
            }
        }

        Validate(cfg, hasInterface);
        return cfg;
    }

    private static void ParseInterfaceKey(WgConfig cfg, string key, string value, int lineNo)
    {
        switch (key)
        {
            case "privatekey":
                if (!IsValidKey(value)) throw Err(lineNo, "некорректный PrivateKey (ожидается ключ base64 длиной 32 байта).");
                cfg.PrivateKey = value;
                break;
            case "address":
                foreach (string part in SplitList(value))
                {
                    if (!IpPrefix.TryParse(part, out var p)) throw Err(lineNo, $"некорректный адрес «{part}».");
                    cfg.Addresses.Add(p);
                }
                break;
            case "dns":
                foreach (string part in SplitList(value))
                    if (IPAddress.TryParse(part, out var ip) && part.Contains(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? ':' : '.'))
                        cfg.Dns.Add(IpMath.StripScope(ip));
                // Не-IP значения в DNS — это домены поиска wg-quick, они не нужны.
                break;
            case "mtu":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int mtu) || mtu < 576 || mtu > 65535)
                    throw Err(lineNo, $"некорректный MTU «{value}».");
                cfg.Mtu = mtu;
                break;
            case "listenport":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port > 65535)
                    throw Err(lineNo, $"некорректный ListenPort «{value}».");
                cfg.ListenPort = port == 0 ? null : port;
                break;
        }
    }

    private static void ParsePeerKey(WgPeer peer, string key, string value, int lineNo)
    {
        switch (key)
        {
            case "publickey":
                if (!IsValidKey(value)) throw Err(lineNo, "некорректный PublicKey (ожидается ключ base64 длиной 32 байта).");
                peer.PublicKey = value;
                break;
            case "presharedkey":
                if (!IsValidKey(value)) throw Err(lineNo, "некорректный PresharedKey (ожидается ключ base64 длиной 32 байта).");
                peer.PresharedKey = value;
                break;
            case "endpoint":
                if (!TryParseEndpoint(value, out string host, out int epPort)) throw Err(lineNo, $"некорректный Endpoint «{value}» (ожидается хост:порт).");
                peer.EndpointHost = host;
                peer.EndpointPort = epPort;
                break;
            case "allowedips":
                foreach (string part in SplitList(value))
                {
                    if (!IpPrefix.TryParse(part, out var p)) throw Err(lineNo, $"некорректная подсеть в AllowedIPs «{part}».");
                    peer.AllowedIps.Add(p.Network);
                }
                break;
            case "persistentkeepalive":
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) { peer.PersistentKeepalive = null; break; }
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ka) || ka > 65535)
                    throw Err(lineNo, $"некорректный PersistentKeepalive «{value}».");
                peer.PersistentKeepalive = ka == 0 ? null : ka;
                break;
        }
    }

    private static void Validate(WgConfig cfg, bool hasInterface)
    {
        if (!hasInterface) throw new WgConfigException("В конфигурации нет секции [Interface].");
        if (cfg.PrivateKey.Length == 0) throw new WgConfigException("В секции [Interface] не указан PrivateKey.");
        if (cfg.Addresses.Count == 0) throw new WgConfigException("В секции [Interface] не указан Address.");
        if (cfg.Peers.Count == 0) throw new WgConfigException("В конфигурации нет секции [Peer].");
        for (int i = 0; i < cfg.Peers.Count; i++)
        {
            var p = cfg.Peers[i];
            string n = cfg.Peers.Count > 1 ? $" №{i + 1}" : "";
            if (p.PublicKey.Length == 0) throw new WgConfigException($"У пира{n} не указан PublicKey.");
            if (p.EndpointHost.Length == 0) throw new WgConfigException($"У пира{n} не указан Endpoint (адрес сервера).");
            if (p.AllowedIps.Count == 0) throw new WgConfigException($"У пира{n} не указан AllowedIPs.");
        }
    }

    private static bool IsValidKey(string value)
    {
        Span<byte> buf = stackalloc byte[33];
        return Convert.TryFromBase64String(value, buf, out int written) && written == 32;
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseEndpoint(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        string portPart;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close < 0 || close + 1 >= value.Length || value[close + 1] != ':') return false;
            host = value[1..close];
            portPart = value[(close + 2)..];
            if (!IPAddress.TryParse(host, out _)) return false;
        }
        else
        {
            int colon = value.LastIndexOf(':');
            if (colon <= 0 || value.IndexOf(':') != colon) return false; // IPv6 без скобок неоднозначен
            host = value[..colon];
            portPart = value[(colon + 1)..];
        }
        return host.Length > 0 &&
               int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
               port is > 0 and <= 65535;
    }

    private static WgConfigException Err(int line, string message) => new($"Строка {line}: {message}");
}
