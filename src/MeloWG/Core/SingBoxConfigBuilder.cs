using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeloWG.Core;

public enum RoutingMode
{
    /// <summary>Весь трафик через туннель.</summary>
    All,
    /// <summary>Через туннель только приложения из списка.</summary>
    Include,
    /// <summary>Через туннель всё, кроме приложений из списка.</summary>
    Exclude,
}

public sealed class RoutingOptions
{
    public RoutingMode Mode { get; init; } = RoutingMode.All;
    public IReadOnlyList<string> AppNames { get; init; } = Array.Empty<string>();
    public bool LanBypass { get; init; } = true;
}

public sealed class CoreParams
{
    public int ApiPort { get; init; }
    public string ApiSecret { get; init; } = "";
    public string InterfaceName { get; init; } = "MeloWG";
    public string LogLevel { get; init; } = "info";
    /// <summary>Назначать ли TUN адрес IPv6. false, если стек IPv6 в Windows отключён.</summary>
    public bool EnableIPv6 { get; init; } = true;
}

/// <summary>Собирает конфиг sing-box: TUN → правила по процессам → WireGuard или напрямую.</summary>
public static class SingBoxConfigBuilder
{
    public const string WgTag = "wg";
    public const string DirectTag = "direct";

    // 198.18.0.0/15 зарезервирован для тестов сетей (RFC 2544) и не встречается в домашних/офисных сетях.
    public const string TunAddress4 = "198.18.0.1/30";
    public const string TunAddress6 = "fdfe:dcba:9876::1/126";

    /// <summary>Что считается «локальной сетью»: частные диапазоны, link-local, мультикаст и широковещание.</summary>
    public static readonly IpSet LocalNetworks = IpSet.From(
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32",
        "fc00::/7", "fe80::/10", "ff00::/8");

    private static readonly string[] LocalDnsSuffixes = { "local", "lan", "home.arpa", "localdomain" };

    public static string Build(WgConfig wg, RoutingOptions routing, CoreParams core)
    {
        var plan = TunnelPlan.Create(wg);
        string? appRegex = BuildProcessRegex(routing.AppNames);

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = core.LogLevel, ["timestamp"] = false },
            ["dns"] = BuildDns(wg, routing, plan),
            ["endpoints"] = new JsonArray(BuildEndpoint(wg)),
            ["inbounds"] = new JsonArray(BuildTun(routing, plan, core)),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag }),
            ["route"] = BuildRoute(routing, plan, appRegex),
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{core.ApiPort}",
                    ["secret"] = core.ApiSecret,
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Регулярное выражение для process_path_regex. В sing-box process_name сравнивается с учётом регистра,
    /// а Windows регистр в путях не различает — поэтому сравниваем имя файла без учёта регистра.
    /// </summary>
    public static string? BuildProcessRegex(IEnumerable<string> appNames)
    {
        var names = appNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return null;
        return @"(?i)(?:^|[\\/])(?:" + string.Join("|", names.Select(EscapeGoRegex)) + ")$";
    }

    /// <summary>Экранирование для синтаксиса RE2 (Go): любой ASCII-символ пунктуации экранируется обратным слэшем.</summary>
    public static string EscapeGoRegex(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            if (c < 128 && !char.IsLetterOrDigit(c) && c != '_' && c != ' ')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static JsonObject BuildEndpoint(WgConfig wg)
    {
        var peers = new JsonArray();
        foreach (var p in wg.Peers)
        {
            var peer = new JsonObject
            {
                ["address"] = p.EndpointHost,
                ["port"] = p.EndpointPort,
                ["public_key"] = p.PublicKey,
                ["allowed_ips"] = StrArray(p.AllowedIps.Select(a => a.ToString())),
            };
            if (p.PresharedKey != null) peer["pre_shared_key"] = p.PresharedKey;
            if (p.PersistentKeepalive != null) peer["persistent_keepalive_interval"] = p.PersistentKeepalive.Value;
            peers.Add(peer);
        }

        var ep = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = WgTag,
            ["system"] = false,
            ["address"] = StrArray(wg.Addresses.Select(a => a.ToString())),
            ["private_key"] = wg.PrivateKey,
            ["peers"] = peers,
        };
        if (wg.Mtu != null) ep["mtu"] = wg.Mtu.Value;
        if (wg.ListenPort != null) ep["listen_port"] = wg.ListenPort.Value;
        return ep;
    }

    private static JsonObject BuildTun(RoutingOptions routing, TunnelPlan plan, CoreParams core)
    {
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = core.InterfaceName,
            // Без IPv6-адреса strict_route блокирует весь IPv6 — утечки мимо туннеля нет.
            ["address"] = StrArray(core.EnableIPv6 ? new[] { TunAddress4, TunAddress6 } : new[] { TunAddress4 }),
            ["auto_route"] = true,
            // На Windows strict_route блокирует DNS-запросы мимо TUN (утечки из-за multihomed DNS).
            ["strict_route"] = true,
            // gVisor не открывает слушающих сокетов, поэтому брандмауэр Windows не задаёт вопросов.
            ["stack"] = "gvisor",
        };

        if (routing.LanBypass)
        {
            // Локальная сеть вообще не заходит в TUN — полная скорость и работа обнаружения устройств.
            // Внутренние сети самого туннеля и адрес TUN из исключения вычитаются.
            var keep = IpSet.From(plan.TunnelNetworks).Union(IpSet.From(TunAddress4, TunAddress6));
            var exclude = LocalNetworks.Subtract(keep).ToPrefixes(core.EnableIPv6 ? null : 4);
            if (exclude.Count > 0) tun["route_exclude_address"] = StrArray(exclude.Select(p => p.ToString()));
        }
        return tun;
    }

    private static JsonObject BuildDns(WgConfig wg, RoutingOptions routing, TunnelPlan plan)
    {
        var servers = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "dns-local" });
        var rules = new JsonArray();
        string final = "dns-local";
        string? strategy = null;

        // В режиме «только выбранные» DNS-запрос нельзя связать с приложением (его делает служба DNS-клиента),
        // поэтому DNS остаётся системным. В остальных режимах — DNS туннеля, чтобы не было утечек.
        if (routing.Mode != RoutingMode.Include)
        {
            IPAddress? dns = wg.Dns.FirstOrDefault(plan.IsTunneled) ?? wg.Dns.FirstOrDefault();
            if (dns == null)
            {
                if (plan.Full4) dns = IPAddress.Parse("1.1.1.1");
                else if (plan.Full6) dns = IPAddress.Parse("2606:4700:4700::1111");
            }

            if (dns != null)
            {
                var server = new JsonObject { ["type"] = "udp", ["tag"] = "dns-vpn", ["server"] = dns.ToString() };
                if (plan.IsTunneled(dns)) server["detour"] = WgTag;
                servers.Add(server);
                final = "dns-vpn";

                if (plan.Has4 && !plan.Has6) strategy = "ipv4_only";
                else if (!plan.Has4 && plan.Has6) strategy = "ipv6_only";

                if (routing.LanBypass)
                    rules.Add(new JsonObject { ["domain_suffix"] = StrArray(LocalDnsSuffixes), ["server"] = "dns-local" });
            }
        }

        var obj = new JsonObject { ["servers"] = servers, ["final"] = final };
        if (rules.Count > 0) obj["rules"] = rules;
        if (strategy != null) obj["strategy"] = strategy;
        return obj;
    }

    private static JsonObject BuildRoute(RoutingOptions routing, TunnelPlan plan, string? appRegex)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["port"] = new JsonArray(53), ["action"] = "sniff", ["sniffer"] = StrArray(new[] { "dns" }) },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
        };

        // Внутренние сети VPN (подсеть интерфейса, частные AllowedIPs) доступны всегда.
        if (plan.TunnelNetworks.Count > 0)
            rules.Add(new JsonObject { ["ip_cidr"] = StrArray(plan.TunnelNetworks.Select(p => p.ToString())), ["outbound"] = WgTag });

        if (routing.LanBypass)
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag });

        // Если AllowedIPs покрывает не весь интернет — адреса вне него идут напрямую, как в обычном WireGuard.
        foreach (int family in new[] { 4, 6 })
        {
            if (!plan.HasFamily(family) || plan.IsFull(family)) continue;
            rules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "and",
                ["rules"] = new JsonArray(
                    new JsonObject { ["ip_version"] = family },
                    new JsonObject { ["ip_cidr"] = StrArray(plan.Allowed.ToPrefixes(family).Select(p => p.ToString())), ["invert"] = true }),
                ["outbound"] = DirectTag,
            });
        }

        // Семейство, которого нет в туннеле (обычно IPv6), для туннелируемого трафика отклоняется,
        // иначе он ушёл бы мимо VPN. Приложение быстро откатится на IPv4.
        var missing = new[] { 4, 6 }.Where(f => !plan.HasFamily(f)).ToList();
        string final;

        switch (routing.Mode)
        {
            case RoutingMode.All:
                foreach (int f in missing) rules.Add(new JsonObject { ["ip_version"] = f, ["action"] = "reject" });
                final = WgTag;
                break;

            case RoutingMode.Include:
                if (appRegex != null)
                {
                    foreach (int f in missing)
                        rules.Add(new JsonObject { ["process_path_regex"] = StrArray(new[] { appRegex }), ["ip_version"] = f, ["action"] = "reject" });
                    rules.Add(new JsonObject { ["process_path_regex"] = StrArray(new[] { appRegex }), ["outbound"] = WgTag });
                }
                final = DirectTag;
                break;

            case RoutingMode.Exclude:
                if (appRegex != null)
                    rules.Add(new JsonObject { ["process_path_regex"] = StrArray(new[] { appRegex }), ["outbound"] = DirectTag });
                foreach (int f in missing) rules.Add(new JsonObject { ["ip_version"] = f, ["action"] = "reject" });
                final = WgTag;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(routing));
        }

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = final,
            // Прямые соединения и сам WireGuard привязываются к физическому адаптеру — без петель через TUN.
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "dns-local",
        };
    }

    private static JsonArray StrArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (string s in items) arr.Add(s);
        return arr;
    }
}

/// <summary>Анализ того, какие адреса реально идут через туннель согласно конфигу.</summary>
internal sealed class TunnelPlan
{
    public IpSet Allowed { get; private init; } = IpSet.Empty;
    public bool Has4 { get; private init; }
    public bool Has6 { get; private init; }
    public bool Full4 { get; private init; }
    public bool Full6 { get; private init; }
    public List<IpPrefix> TunnelNetworks { get; private init; } = new();

    public bool HasFamily(int f) => f == 6 ? Has6 : Has4;
    public bool IsFull(int f) => f == 6 ? Full6 : Full4;

    public bool IsTunneled(IPAddress ip)
    {
        int family = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 6 : 4;
        return HasFamily(family) && Allowed.Contains(ip);
    }

    public static TunnelPlan Create(WgConfig wg)
    {
        var allowedList = wg.Peers.SelectMany(p => p.AllowedIps).ToList();
        var allowed = IpSet.From(allowedList);

        // Семейство адресов работает, только если у интерфейса есть адрес этого семейства и пир его пропускает.
        bool has4 = wg.Addresses.Any(a => !a.IsV6) && allowed.HasFamily(4);
        bool has6 = wg.Addresses.Any(a => a.IsV6) && allowed.HasFamily(6);
        if (!has4 && !has6)
            throw new WgConfigException("Адреса интерфейса (Address) и AllowedIPs пира относятся к разным семействам IP — через такой туннель ничего не пройдёт.");

        // Сети самого VPN: подсети адресов интерфейса и AllowedIPs, целиком лежащие в частных диапазонах
        // (например, 10.8.0.0/24 или удалённая офисная 192.168.50.0/24). 0.0.0.0/0 и 0.0.0.0/1 сюда не входят.
        var nets = new List<IpPrefix>();
        foreach (var a in wg.Addresses)
            if (a.IsV6 ? has6 : has4) nets.Add(a.Network);
        foreach (var p in allowedList)
            if (p.Length > 0 && (p.IsV6 ? has6 : has4) && SingBoxConfigBuilder.LocalNetworks.ContainsPrefix(p)) nets.Add(p);

        return new TunnelPlan
        {
            Allowed = allowed,
            Has4 = has4,
            Has6 = has6,
            Full4 = has4 && allowed.CoversAll(4),
            Full6 = has6 && allowed.CoversAll(6),
            TunnelNetworks = IpSet.From(nets).ToPrefixes(),
        };
    }
}
