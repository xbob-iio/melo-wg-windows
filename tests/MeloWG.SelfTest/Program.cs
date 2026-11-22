using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeloWG.Core;

namespace MeloWG.SelfTest;

/// <summary>
/// Самопроверка ядра клиента без внешних пакетов.
/// Запуск: dotnet run --project tests/MeloWG.SelfTest -- [путь к sing-box.exe]
/// </summary>
internal static class Program
{
    private static int _passed, _failed;

    // Тестовые ключи — валидный base64 длиной 32 байта, реальной ценности не имеют.
    private const string Priv = "yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=";
    private const string Pub = "xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=";
    private const string Psk = "FpCyhws9cxwWoV4xELtfJvjJN+zQVRPISllRWgeopVE=";

    private const string FullV4 = $"""
        [Interface]
        PrivateKey = {Priv}
        Address = 10.66.66.2/32
        DNS = 10.66.66.1, 1.1.1.1
        MTU = 1420

        [Peer]
        PublicKey = {Pub}
        PresharedKey = {Psk}
        Endpoint = vpn.example.com:51820
        AllowedIPs = 0.0.0.0/0, ::/0
        PersistentKeepalive = 25
        """;

    private const string DualStack = $"""
        # комментарий
        [Interface]
        PrivateKey = {Priv}
        Address = 10.8.0.2/24, fd42:42:42::2/64
        DNS = 2606:4700:4700::1111

        [Peer]
        PublicKey = {Pub}
        Endpoint = [2001:db8::1]:443
        AllowedIPs = 0.0.0.0/1, 128.0.0.0/1, ::/1, 8000::/1
        """;

    private const string SplitByIp = $"""
        [Interface]
        PrivateKey = {Priv}
        Address = 10.20.0.5/24
        DNS = 10.20.0.1, corp.local

        [Peer]
        PublicKey = {Pub}
        Endpoint = 203.0.113.10:51820
        AllowedIPs = 10.20.0.0/16, 192.168.50.0/24
        """;

    private const string NoDns = $"""
        [Interface]
        PrivateKey = {Priv}
        Address = 10.9.0.2/32

        [Peer]
        PublicKey = {Pub}
        Endpoint = 198.51.100.7:51820
        AllowedIPs = 0.0.0.0/0
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? singBox = args.Length > 0 ? args[0] : FindSingBox();

        Section("Разбор .conf");
        Test("полный туннель IPv4", () =>
        {
            var c = WgConfig.Parse(FullV4);
            Eq(c.Addresses.Single().ToString(), "10.66.66.2/32");
            Eq(c.Dns.Count, 2);
            Eq(c.Mtu, 1420);
            Eq(c.Peers.Single().EndpointHost, "vpn.example.com");
            Eq(c.Peers.Single().EndpointPort, 51820);
            Eq(c.Peers.Single().PresharedKey, Psk);
            Eq(c.Peers.Single().PersistentKeepalive, 25);
            Eq(c.Peers.Single().AllowedIps.Count, 2);
        });
        Test("IPv6 endpoint в скобках и комментарии", () =>
        {
            var c = WgConfig.Parse(DualStack);
            Eq(c.Peers[0].EndpointHost, "2001:db8::1");
            Eq(c.Peers[0].EndpointPort, 443);
            Eq(c.Addresses.Count, 2);
        });
        Test("домены поиска в DNS пропускаются", () => Eq(WgConfig.Parse(SplitByIp).Dns.Count, 1));
        Test("CRLF и BOM", () => WgConfig.Parse("\uFEFF" + FullV4.Replace("\n", "\r\n")));
        Throws("AmneziaWG отклоняется", () => WgConfig.Parse(FullV4.Replace("MTU = 1420", "Jc = 4\nH1 = 1")), "AmneziaWG");
        Throws("битый ключ", () => WgConfig.Parse(FullV4.Replace(Priv, "abc")), "PrivateKey");
        Throws("нет Endpoint", () => WgConfig.Parse(FullV4.Replace("Endpoint = vpn.example.com:51820", "")), "Endpoint");
        Throws("IPv6 endpoint без скобок", () => WgConfig.Parse(FullV4.Replace("vpn.example.com:51820", "2001:db8::1:51820")), "Endpoint");
        Throws("нет [Peer]", () => WgConfig.Parse(FullV4[..FullV4.IndexOf("[Peer]", StringComparison.Ordinal)]), "[Peer]");

        Section("Подсети");
        Test("0/1 + 128/1 покрывают всё", () => True(IpSet.From("0.0.0.0/1", "128.0.0.0/1").CoversAll(4)));
        Test("10/8 минус 10.8.0.0/24", () =>
        {
            var r = IpSet.From("10.0.0.0/8").Subtract(IpSet.From("10.8.0.0/24")).ToPrefixes();
            Eq(r.Count, 16);
            var back = IpSet.From(r).Union(IpSet.From("10.8.0.0/24")).ToPrefixes();
            Eq(back.Single().ToString(), "10.0.0.0/8");
            False(IpSet.From(r).Contains(System.Net.IPAddress.Parse("10.8.0.77")));
            True(IpSet.From(r).Contains(System.Net.IPAddress.Parse("10.8.1.0")));
        });
        Test("IPv6 ::/0 и вычитание", () =>
        {
            var all = IpSet.From("::/0");
            True(all.CoversAll(6));
            var r = all.Subtract(IpSet.From("fc00::/7")).ToPrefixes();
            False(IpSet.From(r).Contains(System.Net.IPAddress.Parse("fd00::1")));
            True(IpSet.From(r).Contains(System.Net.IPAddress.Parse("2001:db8::1")));
        });
        Test("Network обнуляет хостовые биты", () => Eq(IpPrefix.Parse("10.8.0.2/24").Network.ToString(), "10.8.0.0/24"));

        Section("Регулярка приложений");
        Test("регистр и пути", () =>
        {
            string re = SingBoxConfigBuilder.BuildProcessRegex(new[] { "Chrome.exe", "My App (x86).exe", "Телеграм.exe", "chrome.exe" })!;
            var rx = new Regex(re.Replace("(?i)", ""), RegexOptions.IgnoreCase);
            True(rx.IsMatch(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
            True(rx.IsMatch(@"D:\Games\My App (x86).exe"));
            True(rx.IsMatch(@"C:\Users\u\AppData\Телеграм.EXE"));
            False(rx.IsMatch(@"C:\x\notchrome.exe"));
            False(rx.IsMatch(@"C:\x\chrome.exe.bak"));
            Eq(Regex.Matches(re, "Chrome", RegexOptions.IgnoreCase).Count, 1); // дубликат с другим регистром убран
        });

        Section("Генерация конфига sing-box");
        var cases = new List<(string Name, string Conf)> { ("FullV4", FullV4), ("DualStack", DualStack), ("SplitByIp", SplitByIp), ("NoDns", NoDns) };
        var apps = new[] { "chrome.exe", "Discord.exe", "My App (x86).exe" };
        var built = new List<(string Name, string Json)>();
        foreach (var (name, conf) in cases)
            foreach (RoutingMode mode in Enum.GetValues<RoutingMode>())
                foreach (bool lan in new[] { true, false })
                {
                    string caseName = $"{name}/{mode}/{(lan ? "LAN" : "noLAN")}";
                    Test(caseName, () =>
                    {
                        string json = SingBoxConfigBuilder.Build(WgConfig.Parse(conf),
                            new RoutingOptions { Mode = mode, AppNames = apps, LanBypass = lan },
                            new CoreParams { ApiPort = 19090, ApiSecret = "test" });
                        built.Add((caseName, json));
                        CheckStructure(name, mode, lan, JsonNode.Parse(json)!);
                    });
                }

        Test("пустой список приложений в режиме «только выбранные»", () =>
        {
            string json = SingBoxConfigBuilder.Build(WgConfig.Parse(FullV4),
                new RoutingOptions { Mode = RoutingMode.Include, AppNames = Array.Empty<string>() },
                new CoreParams { ApiPort = 19090, ApiSecret = "t" });
            var route = JsonNode.Parse(json)!["route"]!;
            Eq((string?)route["final"], "direct");
            False(json.Contains("process_path_regex"));
            built.Add(("FullV4/Include/empty", json));
        });

        Test("IPv6 отключён в Windows: TUN только с IPv4", () =>
        {
            string json = SingBoxConfigBuilder.Build(WgConfig.Parse(DualStack),
                new RoutingOptions { Mode = RoutingMode.Exclude, AppNames = apps, LanBypass = true },
                new CoreParams { ApiPort = 19090, ApiSecret = "t", EnableIPv6 = false });
            var tun = JsonNode.Parse(json)!["inbounds"]![0]!;
            Eq(tun["address"]!.AsArray().Count, 1);
            False(tun["route_exclude_address"]!.AsArray().Any(n => ((string)n!).Contains(':')));
            built.Add(("DualStack/Exclude/noIPv6", json));
        });

        Section("DPAPI");
        Test("шифрование туда и обратно", () =>
        {
            byte[] data = Encoding.UTF8.GetBytes(FullV4);
            byte[] enc = Native.Protect(data);
            False(enc.AsSpan().SequenceEqual(data));
            True(Native.Unprotect(enc).AsSpan().SequenceEqual(data));
        });
        Test("хранилище: туннель и настройки", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "melowg-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var st = new Storage(dir);
                var rec = st.AddTunnel("Тест", FullV4);
                False(File.ReadAllText(Path.Combine(st.TunnelsDir, rec.Id + ".json")).Contains(Priv));
                Eq(st.ReadTunnelConfig(st.LoadTunnels().Single()), FullV4);
                st.SaveSettings(new AppSettings { Mode = RoutingMode.Exclude, Apps = { new AppEntry { Name = "a.exe" } } });
                Eq(st.LoadSettings().Mode, RoutingMode.Exclude);
                st.DeleteTunnel(rec.Id);
                Eq(st.LoadTunnels().Count, 0);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Section("Проверка конфигов ядром (sing-box check)");
        if (singBox == null || !File.Exists(singBox))
        {
            Console.WriteLine("  ! sing-box.exe не найден — проверка ядром пропущена");
            _failed++;
        }
        else
        {
            foreach (var (name, json) in built)
                Test(name, () => SingBoxCheck(singBox, json));
        }

        Section("Запуск ядра без TUN (stdin, старт, трафик, остановка Ctrl+C)");
        if (singBox != null && File.Exists(singBox))
        {
            Test("CoreRunner: два цикла запуск/остановка", () => RunnerCycle(singBox).GetAwaiter().GetResult());
            Test("CoreRunner: ошибка конфига понятна", () =>
            {
                var runner = new CoreRunner(singBox, Path.Combine(Path.GetTempPath(), "melowg-selftest-core"));
                try
                {
                    runner.StartAsync("{\"inbounds\":[{\"type\":\"nope\"}]}").GetAwaiter().GetResult();
                    throw new Exception("ожидалась ошибка");
                }
                catch (CoreException ex)
                {
                    if (!ex.Message.Contains("nope", StringComparison.OrdinalIgnoreCase)) throw new Exception("непонятная ошибка: " + ex.Message);
                }
                finally { runner.Dispose(); }
            });
        }

        Console.WriteLine();
        Console.WriteLine($"Итого: пройдено {_passed}, провалено {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void CheckStructure(string conf, RoutingMode mode, bool lan, JsonNode root)
    {
        var route = root["route"]!;
        var rules = route["rules"]!.AsArray();
        string final = (string)route["final"]!;
        Eq(final, mode == RoutingMode.Include ? "direct" : "wg");

        bool hasRegex = rules.Any(r => r?["process_path_regex"] != null);
        Eq(hasRegex, mode != RoutingMode.All);

        var tun = root["inbounds"]![0]!;
        Eq(tun["route_exclude_address"] != null, lan);
        if (lan)
        {
            var excl = IpSet.From(tun["route_exclude_address"]!.AsArray().Select(n => IpPrefix.Parse((string)n!)));
            True(excl.Contains(System.Net.IPAddress.Parse("192.168.1.1")));
            False(excl.Contains(System.Net.IPAddress.Parse("198.18.0.2"))); // адрес DNS внутри TUN
            if (conf == "DualStack") False(excl.Contains(System.Net.IPAddress.Parse("10.8.0.1")));
            if (conf == "SplitByIp")
            {
                False(excl.Contains(System.Net.IPAddress.Parse("10.20.5.5")));
                False(excl.Contains(System.Net.IPAddress.Parse("192.168.50.10")));
                True(excl.Contains(System.Net.IPAddress.Parse("192.168.1.10")));
            }
        }

        var dns = root["dns"]!;
        string dnsFinal = (string)dns["final"]!;
        if (mode == RoutingMode.Include) Eq(dnsFinal, "dns-local");
        else Eq(dnsFinal, "dns-vpn");

        if (mode != RoutingMode.Include)
        {
            var vpn = dns["servers"]!.AsArray().First(s => (string?)s!["tag"] == "dns-vpn")!;
            switch (conf)
            {
                case "FullV4":
                    Eq((string?)vpn["server"], "10.66.66.1");
                    Eq((string?)vpn["detour"], "wg");
                    Eq((string?)dns["strategy"], "ipv4_only"); // у интерфейса нет IPv6-адреса
                    break;
                case "DualStack":
                    Eq((string?)vpn["server"], "2606:4700:4700::1111");
                    Eq(dns["strategy"], null);
                    break;
                case "SplitByIp":
                    Eq((string?)vpn["detour"], "wg");
                    break;
                case "NoDns":
                    Eq((string?)vpn["server"], "1.1.1.1");
                    break;
            }
        }

        // У FullV4 нет IPv6-адреса: туннелируемый IPv6 должен отклоняться, а не уходить мимо VPN.
        bool rejects6 = rules.Any(r => (string?)r?["action"] == "reject" && (int?)r?["ip_version"] == 6);
        if (conf is "FullV4" or "NoDns" or "SplitByIp") True(rejects6);
        if (conf == "DualStack") False(rejects6);

        // Split по IP: адреса вне AllowedIPs должны идти напрямую.
        bool outsideDirect = rules.Any(r => (string?)r?["type"] == "logical");
        Eq(outsideDirect, conf == "SplitByIp");
    }

    private static async System.Threading.Tasks.Task RunnerCycle(string exe)
    {
        int apiPort = FreePort(), proxyPort = FreePort();
        string json = $$"""
            {
              "log": { "level": "info", "timestamp": false },
              "inbounds": [ { "type": "mixed", "tag": "in", "listen": "127.0.0.1", "listen_port": {{proxyPort}} } ],
              "outbounds": [ { "type": "direct", "tag": "direct" } ],
              "experimental": { "clash_api": { "external_controller": "127.0.0.1:{{apiPort}}", "secret": "s3cret" } }
            }
            """;
        using var runner = new CoreRunner(exe, Path.Combine(Path.GetTempPath(), "melowg-selftest-core"));
        var log = new List<string>();
        runner.LogLine += l => { lock (log) log.Add(l); };
        bool crashed = false;
        runner.Crashed += _ => crashed = true;

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            await runner.StartAsync(json);
            True(runner.IsRunning);

            using var mon = new TrafficMonitor();
            int samples = 0;
            mon.Sample += (_, _) => System.Threading.Interlocked.Increment(ref samples);
            mon.Start(apiPort, "s3cret");
            var sw = Stopwatch.StartNew();
            while (samples == 0 && sw.Elapsed < TimeSpan.FromSeconds(6)) await System.Threading.Tasks.Task.Delay(100);
            mon.Stop();
            if (samples == 0) throw new Exception("нет данных от /traffic");

            sw.Restart();
            await runner.StopAsync();
            False(runner.IsRunning);
            // Корректная остановка по Ctrl+C укладывается в секунды; принудительное завершение — только после 6 с.
            if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception($"цикл {cycle}: остановка заняла {sw.Elapsed.TotalSeconds:F1} с — Ctrl+C не сработал");
        }
        False(crashed);
        lock (log)
            if (!log.Any(l => l.Contains("sing-box started"))) throw new Exception("нет строки о запуске в журнале");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void SingBoxCheck(string exe, string json)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string a in new[] { "check", "-c", "stdin", "--disable-color" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(json);
        p.StandardInput.Close();
        string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(); throw new Exception("таймаут sing-box check"); }
        if (p.ExitCode != 0) throw new Exception($"sing-box check: код {p.ExitCode}: {err.Trim()}");
        if (err.Contains("WARN", StringComparison.Ordinal) || err.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"предупреждение sing-box: {err.Trim()}");
    }

    private static string? FindSingBox()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "core", "sing-box.exe");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static void Section(string name) => Console.WriteLine($"\n== {name}");

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  + {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  - {name}: {ex.Message}");
        }
    }

    private static void Throws(string name, Action body, string expectedFragment) => Test(name, () =>
    {
        try
        {
            body();
        }
        catch (WgConfigException ex)
        {
            if (!ex.Message.Contains(expectedFragment, StringComparison.Ordinal))
                throw new Exception($"не то сообщение: {ex.Message}");
            return;
        }
        throw new Exception("исключение не выброшено");
    });

    private static void Eq<T>(T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"ожидалось «{expected}», получено «{actual}»");
    }

    private static void True(bool v) { if (!v) throw new Exception("ожидалось true"); }
    private static void False(bool v) { if (v) throw new Exception("ожидалось false"); }
}
