using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

/// <summary>
/// Manages the child xray.exe process lifecycle with hidden window,
/// async log draining, port preflight, and Job Object lifetime binding.
/// </summary>
public sealed class XrayProcessManager : IDisposable
{
    private readonly object _sync = new();
    private readonly JobObject _job = new();
    private readonly ConcurrentQueue<string> _recentLogs = new();

    private Process? _process;
    private bool _expectingExit;
    private bool _disposed;

    public event EventHandler<string?>? UnexpectedExit;

    public event EventHandler<string>? LogLineReceived;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public IReadOnlyList<int> ActivePorts { get; private set; } = [];

    public bool IsTgBridgeActive { get; private set; }

    public async Task StartAsync(AppSettings settings, string? currentLanIp, TgWsProxyStatus? tgStatus, CancellationToken ct = default)
    {
        var warpActive = settings.WarpMode != WarpRoutingMode.Off &&
                         settings.WarpAccount is { IsValid: true };
        var maxAttempts = warpActive ? 3 : 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            int? warpProbePort = warpActive ? GetFreeLoopbackPort() : null;

            await StartSingleAttemptAsync(settings, currentLanIp, tgStatus, warpProbePort, ct).ConfigureAwait(false);

            if (!warpActive || warpProbePort is not > 0)
                return;

            if (await WarmupWarpAsync(warpProbePort.Value, ct).ConfigureAwait(false))
                return;

            AppendLog($"[WARP] Попытка {attempt}/{maxAttempts} не прошла (ТСПУ), перезапуск сокета...");
            Stop();
        }

        throw new InvalidOperationException(
            "Не удалось установить соединение с Cloudflare WARP после 3 попыток. Попробуйте нажать «Тест» рядом с WARP для подбора другого endpoint.");
    }

    private async Task StartSingleAttemptAsync(
        AppSettings settings,
        string? currentLanIp,
        TgWsProxyStatus? tgStatus,
        int? warpProbePort,
        CancellationToken ct)
    {
        Stop();

        if (!File.Exists(AppSettings.XrayExePath))
            throw new FileNotFoundException("Файл xray.exe не найден.", AppSettings.XrayExePath);

        var tgBridgeActive = settings.EnableTgBridge &&
                             tgStatus is { IsReadyForBridge: true } &&
                             settings.TgBridgePort != settings.Port;

        EnsurePortFree(settings.Port);
        if (tgBridgeActive)
            EnsurePortFree(settings.TgBridgePort);

        var configPath = XrayConfigBuilder.WriteRuntimeConfig(settings, currentLanIp, tgStatus, warpProbePort);
        while (_recentLogs.TryDequeue(out _)) { }

        var startInfo = new ProcessStartInfo
        {
            FileName = AppSettings.XrayExePath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = AppSettings.BinDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var proc = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        proc.OutputDataReceived += (_, e) => AppendLog(e.Data);
        proc.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        proc.Exited += OnProcessExited;

        lock (_sync)
        {
            if (_disposed)
            {
                proc.Dispose();
                throw new ObjectDisposedException(nameof(XrayProcessManager));
            }

            _expectingExit = false;
            if (!proc.Start())
            {
                proc.Dispose();
                throw new InvalidOperationException("Не удалось запустить процесс xray.exe.");
            }

            _job.AssignProcess(proc.Handle);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _process = proc;
            IsTgBridgeActive = tgBridgeActive;
            ActivePorts = tgBridgeActive
                ? [settings.Port, settings.TgBridgePort]
                : [settings.Port];
        }

        await Task.Delay(350, ct).ConfigureAwait(false);

        bool exitedEarly;
        int exitCode = 0;
        lock (_sync)
        {
            exitedEarly = _process is null || _process.HasExited;
            if (exitedEarly && _process is not null)
            {
                exitCode = _process.ExitCode;
            }
        }

        if (exitedEarly)
        {
            Stop();
            var tail = GetRecentLogTail();
            var details = string.IsNullOrWhiteSpace(tail)
                ? $"xray.exe завершился сразу после запуска (код {exitCode})."
                : $"xray.exe завершился с ошибкой (код {exitCode}):\n{tail}";
            throw new InvalidOperationException(details);
        }
    }

    public void Stop()
    {
        Process? toStop;
        lock (_sync)
        {
            _expectingExit = true;
            toStop = _process;
            _process = null;
            ActivePorts = [];
            IsTgBridgeActive = false;
        }

        if (toStop is null)
            return;

        try
        {
            toStop.Exited -= OnProcessExited;
            if (!toStop.HasExited)
            {
                toStop.Kill(entireProcessTree: true);
                toStop.WaitForExit(2000);
            }
        }
        catch
        {
            // Ignore race if process already exited.
        }
        finally
        {
            toStop.Dispose();
        }
    }

    public string GetRecentLogTail() => string.Join(Environment.NewLine, _recentLogs);

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        _recentLogs.Enqueue(line);
        while (_recentLogs.Count > 250)
        {
            _recentLogs.TryDequeue(out _);
        }

        LogLineReceived?.Invoke(this, line);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool unexpected;
        lock (_sync)
        {
            unexpected = !_expectingExit && ReferenceEquals(_process, sender);
            if (unexpected)
            {
                _process = null;
                ActivePorts = [];
                IsTgBridgeActive = false;
            }
        }

        if (unexpected)
        {
            if (sender is Process p)
            {
                p.Exited -= OnProcessExited;
                p.Dispose();
            }
            UnexpectedExit?.Invoke(this, GetRecentLogTail());
        }
    }

    private static async Task<bool> WarmupWarpAsync(int socksPort, CancellationToken ct)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(4500) };
            using var resp = await http.GetAsync("https://cp.cloudflare.com/generate_204", ct).ConfigureAwait(false);
            return (int)resp.StatusCode is 204 or 200;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void EnsurePortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException($"Порт {port} уже занят другим приложением.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _job.Dispose();
    }
}
