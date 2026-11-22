using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeloWG.Core;

public sealed class CoreException : Exception
{
    public CoreException(string message) : base(message) { }
}

/// <summary>Запуск и остановка процесса sing-box. Конфиг передаётся через stdin и на диск не пишется.</summary>
public sealed class CoreRunner : IDisposable
{
    private static readonly object ConsoleLock = new();

    private readonly string _exePath;
    private readonly string _workDir;
    private readonly IntPtr _job;
    private readonly object _sync = new();
    private readonly Queue<string> _recentProblems = new();

    private Process? _process;
    private TaskCompletionSource<bool>? _started;
    private bool _stopRequested;

    public CoreRunner(string exePath, string workDir)
    {
        _exePath = exePath;
        _workDir = workDir;
        _job = Native.CreateKillOnCloseJob();
    }

    /// <summary>Строка журнала ядра (из любого потока).</summary>
    public event Action<string>? LogLine;

    /// <summary>Ядро завершилось само, без запроса на остановку. Аргумент — причина (из любого потока).</summary>
    public event Action<string>? Crashed;

    public bool IsRunning
    {
        get { lock (_sync) return _process is { HasExited: false }; }
    }

    public string ExePath => _exePath;

    public async Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (!File.Exists(_exePath))
            throw new CoreException($"Не найдено ядро: {_exePath}");

        Process process;
        TaskCompletionSource<bool> started;
        lock (_sync)
        {
            if (_process is { HasExited: false }) throw new CoreException("Ядро уже запущено.");
            _recentProblems.Clear();
            _stopRequested = false;

            Directory.CreateDirectory(_workDir);
            var psi = new ProcessStartInfo(_exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _workDir,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string arg in new[] { "run", "-c", "stdin", "-D", _workDir, "--disable-color" })
                psi.ArgumentList.Add(arg);

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _started = started;
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Exited += (_, _) => OnExited(process);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                _started = null;
                throw new CoreException($"Не удалось запустить ядро: {ex.Message}");
            }

            if (_job != IntPtr.Zero) Native.AssignProcessToJobObject(_job, process.Handle);
            _process = process;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await using (var stdin = process.StandardInput)
            {
                await stdin.WriteAsync(configJson.AsMemory(), ct);
            }
        }
        catch (IOException)
        {
            // Процесс завершился раньше, чем прочитал конфиг, — причину покажет OnExited.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await started.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            await StopAsync();
            throw new CoreException(ct.IsCancellationRequested ? "Подключение отменено." : "Ядро не запустилось за 25 секунд. Подробности в журнале.");
        }
        catch (CoreException)
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _stopRequested = true;
        }
        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                bool signalled = SendCtrlC(process.Id);
                if (signalled)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    try { await process.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
                }
                RestoreCtrlC();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершён.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process)) _process = null;
            }
            process.Dispose();
        }
    }

    /// <summary>Мгновенная остановка (выход из системы, аварийное завершение клиента).</summary>
    public void KillNow()
    {
        lock (_sync)
        {
            _stopRequested = true;
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = line.TrimEnd();

        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("FATAL", StringComparison.Ordinal))
        {
            lock (_recentProblems)
            {
                _recentProblems.Enqueue(line);
                while (_recentProblems.Count > 5) _recentProblems.Dequeue();
            }
        }

        if (line.Contains("sing-box started", StringComparison.Ordinal))
            _started?.TrySetResult(true);

        LogLine?.Invoke(line);
    }

    private void OnExited(Process process)
    {
        // Дочитываем буферизованный вывод, чтобы причина ошибки не потерялась.
        try { process.WaitForExit(); } catch { }

        int code;
        try { code = process.ExitCode; } catch { code = -1; }

        string reason = DescribeFailure(code);
        var started = _started;
        bool wasStarting = started != null && !started.Task.IsCompleted;
        bool requested;
        lock (_sync) requested = _stopRequested;

        LogLine?.Invoke($"[клиент] ядро завершилось, код {code}");

        if (wasStarting)
            started!.TrySetException(new CoreException(reason));
        else if (!requested)
            Crashed?.Invoke(reason);
    }

    private string DescribeFailure(int code)
    {
        string? last;
        lock (_recentProblems) last = _recentProblems.LastOrDefault();
        if (last == null) return $"Ядро завершилось с кодом {code}. Подробности в журнале.";

        // «FATAL[0000] start service: …» → «start service: …»
        int bracket = last.IndexOf(']');
        string msg = bracket >= 0 && bracket < 20 ? last[(bracket + 1)..].Trim() : last;
        foreach (string level in new[] { "FATAL", "ERROR" })
            if (msg.StartsWith(level, StringComparison.Ordinal)) msg = msg[level.Length..].Trim();
        return Humanize(msg);
    }

    private static string Humanize(string msg)
    {
        if (msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || msg.Contains("access denied", StringComparison.OrdinalIgnoreCase))
            return "Нет прав на создание сетевого адаптера. Запустите клиент от имени администратора.\n\n" + msg;
        if (msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || msg.Contains("Only one usage of each socket", StringComparison.OrdinalIgnoreCase))
            return "Порт уже занят другой программой.\n\n" + msg;
        if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase) && msg.Contains("tun", StringComparison.OrdinalIgnoreCase))
            return "Сетевой адаптер с таким именем уже существует — возможно, запущена другая копия ядра.\n\n" + msg;
        return msg;
    }

    private static bool SendCtrlC(int pid)
    {
        lock (ConsoleLock)
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) return false;
            // Пока подключены к чужой консоли, сами Ctrl+C игнорируем.
            Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
            bool ok = Native.GenerateConsoleCtrlEvent(Native.CTRL_C_EVENT, 0);
            Native.FreeConsole();
            return ok;
        }
    }

    private static void RestoreCtrlC()
    {
        // Флаг «игнорировать Ctrl+C» наследуется дочерними процессами — снимаем его,
        // иначе следующий запуск ядра нельзя будет корректно остановить.
        lock (ConsoleLock) Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    public void Dispose()
    {
        KillNow();
        if (_job != IntPtr.Zero) Native.CloseHandle(_job);
    }
}
