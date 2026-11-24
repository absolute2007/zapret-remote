using System.Diagnostics;
using System.Text.Json;

namespace ZapretRemote.Services;

public sealed record TgWsProxyStatus(
    bool IsProcessRunning,
    int[] Pids,
    bool HasConfig,
    string LocalHost,
    int LocalPort,
    string? RawSecret,
    bool NoSecure)
{
    public string? FullSecret
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RawSecret))
                return null;

            var s = RawSecret.Trim().ToLowerInvariant();
            if (!NoSecure && s.Length == 32 && !s.StartsWith("dd", StringComparison.Ordinal) && !s.StartsWith("ee", StringComparison.Ordinal))
                return "dd" + s;

            return s;
        }
    }

    public bool IsReadyForBridge =>
        HasConfig &&
        LocalPort is >= 1 and <= 65535 &&
        !string.IsNullOrEmpty(FullSecret);
}

/// <summary>
/// Strictly read-only detector for Flowseal/tg-ws-proxy process and %APPDATA%\TgWsProxy\config.json.
/// </summary>
public static class TgWsProxyService
{
    private static readonly string[] CandidateProcessNames =
    [
        "TgWsProxy_windows",
        "TgWsProxy",
        "tg-ws-proxy"
    ];

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TgWsProxy",
        "config.json");

    public static TgWsProxyStatus GetStatus()
    {
        var pids = FindProcessIds();
        var isRunning = pids.Length > 0;

        var hasConfig = false;
        var host = "127.0.0.1";
        var port = 1080;
        string? secret = null;
        var noSecure = false;

        if (File.Exists(ConfigPath))
        {
            try
            {
                using var fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;

                if (root.TryGetProperty("host", out var hostProp) && hostProp.ValueKind == JsonValueKind.String)
                {
                    var rawHost = hostProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(rawHost) && rawHost != "0.0.0.0")
                        host = rawHost;
                }

                if (root.TryGetProperty("port", out var portProp) && portProp.TryGetInt32(out var parsedPort))
                {
                    port = parsedPort;
                }

                if (root.TryGetProperty("secret", out var secretProp) && secretProp.ValueKind == JsonValueKind.String)
                {
                    secret = secretProp.GetString()?.Trim();
                }

                if (root.TryGetProperty("no_secure", out var noSecProp) &&
                    noSecProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    noSecure = noSecProp.GetBoolean();
                }

                hasConfig = !string.IsNullOrWhiteSpace(secret) && port is >= 1 and <= 65535;
            }
            catch
            {
                hasConfig = false;
            }
        }

        return new TgWsProxyStatus(isRunning, pids, hasConfig, host, port, secret, noSecure);
    }

    public static string BuildTelegramProxyHttpsUri(string lanIp, int lanPort, string fullSecret)
    {
        var host = string.IsNullOrWhiteSpace(lanIp) ? "127.0.0.1" : lanIp.Trim();
        return $"https://t.me/proxy?server={host}&port={lanPort}&secret={Uri.EscapeDataString(fullSecret)}";
    }

    public static string BuildTelegramProxyDeepLink(string lanIp, int lanPort, string fullSecret)
    {
        var host = string.IsNullOrWhiteSpace(lanIp) ? "127.0.0.1" : lanIp.Trim();
        return $"tg://proxy?server={host}&port={lanPort}&secret={Uri.EscapeDataString(fullSecret)}";
    }

    private static int[] FindProcessIds()
    {
        var pids = new List<int>();
        foreach (var name in CandidateProcessNames)
        {
            Process[] procs = [];
            try
            {
                procs = Process.GetProcessesByName(name);
                foreach (var proc in procs)
                {
                    try
                    {
                        if (!pids.Contains(proc.Id))
                            pids.Add(proc.Id);
                    }
                    catch
                    {
                        // Process may have exited.
                    }
                }
            }
            catch
            {
                // Ignore enumeration error.
            }
            finally
            {
                foreach (var proc in procs)
                {
                    proc.Dispose();
                }
            }
        }
        return [.. pids];
    }
}
