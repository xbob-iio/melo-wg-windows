using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeloWG.Core;

/// <summary>Читает поток /traffic из Clash API sing-box: раз в секунду {"up": байт/с, "down": байт/с}.</summary>
public sealed class TrafficMonitor : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _cts;

    /// <summary>Скорость отдачи и загрузки, байт/с (из фонового потока).</summary>
    public event Action<long, long>? Sample;

    public void Start(int port, string secret)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => LoopAsync(port, secret, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(int port, string secret, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/traffic");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    using var doc = JsonDocument.Parse(line);
                    long up = doc.RootElement.TryGetProperty("up", out var u) ? u.GetInt64() : 0;
                    long down = doc.RootElement.TryGetProperty("down", out var d) ? d.GetInt64() : 0;
                    Sample?.Invoke(up, down);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // API ещё не поднялось или соединение оборвалось — повторим.
            }

            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
