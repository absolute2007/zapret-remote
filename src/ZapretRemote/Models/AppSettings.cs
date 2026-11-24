using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretRemote.Models;

/// <summary>
/// Routing mode for built-in Cloudflare WARP outbound.
/// </summary>
public enum WarpRoutingMode
{
    Off = 0,
    Selective = 1,
    AllExceptRu = 2
}

/// <summary>
/// Stored Cloudflare WARP WireGuard credentials and verified endpoint.
/// </summary>
public sealed class WarpAccountData
{
    public const string DefaultEndpoint = "162.159.192.1:2408";

    public string PrivateKey { get; set; } = string.Empty;

    public string PublicKey { get; set; } = string.Empty;

    public string PeerPublicKey { get; set; } = string.Empty;

    public string AddressV4 { get; set; } = string.Empty;

    public string AddressV6 { get; set; } = string.Empty;

    public int[] Reserved { get; set; } = [];

    public string Endpoint { get; set; } = DefaultEndpoint;

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PrivateKey) &&
        !string.IsNullOrWhiteSpace(PeerPublicKey) &&
        !string.IsNullOrWhiteSpace(AddressV4) &&
        !string.IsNullOrWhiteSpace(AddressV6) &&
        Reserved is { Length: 3 } &&
        !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>
/// Persistent application settings stored next to the executable.
/// </summary>
public sealed class AppSettings
{
    public const int DefaultPort = 10808;
    public const int DefaultTgBridgePort = 10809;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Uuid { get; set; } = string.Empty;

    public int Port { get; set; } = DefaultPort;

    public string? PreferredIp { get; set; }

    public bool BlockQuic { get; set; } = true;

    public bool EnableTgBridge { get; set; } = true;

    public int TgBridgePort { get; set; } = DefaultTgBridgePort;

    public bool VerboseLogging { get; set; } = false;

    public WarpRoutingMode WarpMode { get; set; } = WarpRoutingMode.Off;

    public WarpAccountData? WarpAccount { get; set; }

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string BinDirectory => Path.Combine(AppContext.BaseDirectory, "bin");

    public static string XrayExePath => Path.Combine(BinDirectory, "xray.exe");

    public static string RuntimeConfigPath => Path.Combine(BinDirectory, "xray.runtime.json");

    public static string ZapretBackupPath => Path.Combine(AppContext.BaseDirectory, "zapret-backup.json");

    public IReadOnlyCollection<int> GetRequiredFirewallPorts(bool tgBridgeActive)
    {
        return tgBridgeActive && EnableTgBridge && TgBridgePort != Port
            ? [Port, TgBridgePort]
            : [Port];
    }

    public static AppSettings Load()
    {
        AppSettings? loaded = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch
            {
                loaded = null;
            }
        }

        var settings = loaded ?? new AppSettings();
        var changed = loaded is null;

        if (!Guid.TryParse(settings.Uuid, out var parsed) || parsed == Guid.Empty)
        {
            settings.Uuid = Guid.NewGuid().ToString("D");
            changed = true;
        }
        else
        {
            settings.Uuid = parsed.ToString("D");
        }

        if (settings.Port < MinPort || settings.Port > MaxPort)
        {
            settings.Port = DefaultPort;
            changed = true;
        }

        if (settings.TgBridgePort < MinPort || settings.TgBridgePort > MaxPort || settings.TgBridgePort == settings.Port)
        {
            settings.TgBridgePort = settings.Port == DefaultTgBridgePort ? DefaultTgBridgePort + 1 : DefaultTgBridgePort;
            changed = true;
        }

        if (changed)
        {
            settings.Save();
        }

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppContext.BaseDirectory);
        var tmpPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, SettingsPath, overwrite: true);
    }

    public string RegenerateUuid()
    {
        Uuid = Guid.NewGuid().ToString("D");
        Save();
        return Uuid;
    }
}
