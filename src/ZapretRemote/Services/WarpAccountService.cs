using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

/// <summary>
/// Registers free Cloudflare WARP WireGuard credentials and probes endpoints
/// through Xray-core with UDP noise DPI bypass.
/// </summary>
public static class WarpAccountService
{
    private const string CloudflareRegUrl = "https://api.cloudflareclient.com/v0a2158/reg";

    private static readonly string[] CandidateEndpoints =
    [
        "162.159.192.1:2408",
        "162.159.193.1:2408",
        "162.159.195.1:2408",
        "162.159.192.5:2408",
        "188.114.96.1:2408",
        "188.114.97.1:2408",
        "188.114.98.1:2408",
        "188.114.99.1:2408"
    ];

    private static readonly HttpClient RegHttp = CreateRegHttpClient();

    private static HttpClient CreateRegHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "okhttp/3.12.1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("CF-Client-Version", "a-6.11-2223");
        return client;
    }

    public static async Task<WarpAccountData> EnsureAccountAsync(
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (settings.WarpAccount is { IsValid: true } existing)
            return existing;

        if (!File.Exists(AppSettings.XrayExePath))
            throw new FileNotFoundException("Файл xray.exe не найден для генерации ключей WARP.", AppSettings.XrayExePath);

        progress?.Report("Генерация ключей X25519 для WARP...");
        var (privKey, pubKey) = await GenerateX25519KeypairAsync(ct).ConfigureAwait(false);

        progress?.Report("Регистрация аккаунта Cloudflare WARP...");
        var account = await RegisterAccountAsync(privKey, pubKey, ct).ConfigureAwait(false);

        progress?.Report("Поиск рабочего endpoint WARP...");
        var (workingEndpoint, _) = await FindWorkingEndpointAsync(account, progress, ct).ConfigureAwait(false);
        account.Endpoint = workingEndpoint;

        settings.WarpAccount = account;
        settings.Save();
        return account;
    }

    public static async Task<(string Endpoint, string ExitIp)> FindWorkingEndpointAsync(
        WarpAccountData account,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(account.Endpoint))
        {
            candidates.Add(account.Endpoint.Trim());
        }

        foreach (var ep in CandidateEndpoints)
        {
            if (!candidates.Contains(ep, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(ep);
            }
        }

        string? lastError = null;
        foreach (var ep in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Проверка WARP ({ep})...");

            var (success, exitIp, details) = await TestEndpointAsync(account, ep, ct).ConfigureAwait(false);
            if (success && !string.IsNullOrWhiteSpace(exitIp))
            {
                return (ep, exitIp);
            }

            lastError = $"{ep}: {details ?? "не отвечает"}";
        }

        throw new InvalidOperationException(
            $"Не удалось подключиться ни к одному endpoint Cloudflare WARP. Последняя ошибка: {lastError ?? "таймаут"}");
    }

    public static async Task<(bool Success, string? ExitIp, string? Details)> TestEndpointAsync(
        WarpAccountData account,
        string endpoint,
        CancellationToken ct)
    {
        (bool Success, string? ExitIp, string? Details) lastResult = (false, null, "таймаут");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lastResult = await TestEndpointOnceAsync(account, endpoint, ct).ConfigureAwait(false);
            if (lastResult.Success)
                return lastResult;
        }
        return lastResult;
    }

    private static async Task<(bool Success, string? ExitIp, string? Details)> TestEndpointOnceAsync(
        WarpAccountData account,
        string endpoint,
        CancellationToken ct)
    {
        var socksPort = GetFreeTcpPort();
        var cfgPath = Path.Combine(Path.GetTempPath(), $"zr-warp-test-{Guid.NewGuid():N}.json");

        var cfg = new
        {
            log = new { loglevel = "warning" },
            dns = new
            {
                servers = new[]
                {
                    "https+local://1.1.1.1/dns-query",
                    "https+local://8.8.8.8/dns-query"
                },
                queryStrategy = "UseIPv4"
            },
            inbounds = new object[]
            {
                new
                {
                    tag = "socks-in",
                    listen = "127.0.0.1",
                    port = socksPort,
                    protocol = "socks",
                    settings = new { udp = true }
                }
            },
            outbounds = new object[]
            {
                new
                {
                    tag = "direct",
                    protocol = "freedom",
                    settings = new { }
                },
                new
                {
                    tag = "warp",
                    protocol = "wireguard",
                    settings = new
                    {
                        secretKey = account.PrivateKey,
                        address = new[]
                        {
                            $"{account.AddressV4}/32",
                            $"{account.AddressV6}/128"
                        },
                        peers = new[]
                        {
                            new
                            {
                                publicKey = account.PeerPublicKey,
                                allowedIPs = new[] { "0.0.0.0/0", "::/0" },
                                endpoint,
                                keepAlive = 20
                            }
                        },
                        reserved = account.Reserved,
                        mtu = 1280,
                        domainStrategy = "ForceIPv4"
                    },
                    streamSettings = new
                    {
                        sockopt = new
                        {
                            dialerProxy = "warp-noise"
                        }
                    }
                },
                new
                {
                    tag = "warp-noise",
                    protocol = "freedom",
                    settings = new
                    {
                        noises = new object[]
                        {
                            new { type = "rand", packet = "10-30", delay = "10-20" },
                            new { type = "rand", packet = "30-60", delay = "10-20" },
                            new { type = "rand", packet = "60-120", delay = "10-20" },
                            new { type = "rand", packet = "120-200", delay = "10-20" },
                            new { type = "rand", packet = "40-90", delay = "10-20" }
                        }
                    }
                }
            },
            routing = new
            {
                domainStrategy = "AsIs",
                rules = new object[]
                {
                    new
                    {
                        type = "field",
                        inboundTag = new[] { "socks-in" },
                        outboundTag = "warp"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(cfgPath, JsonSerializer.Serialize(cfg), ct).ConfigureAwait(false);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AppSettings.XrayExePath,
                Arguments = $"run -c \"{cfgPath}\"",
                WorkingDirectory = AppSettings.BinDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };

        try
        {
            if (!proc.Start())
                return (false, null, "не удалось запустить xray.exe");

            await Task.Delay(500, ct).ConfigureAwait(false);
            if (proc.HasExited)
                return (false, null, $"xray.exe завершился с кодом {proc.ExitCode}");

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

            using var probeResp = await http.GetAsync("https://cp.cloudflare.com/generate_204", ct).ConfigureAwait(false);
            if ((int)probeResp.StatusCode is not (204 or 200))
                return (false, null, $"cp.cloudflare.com вернул {(int)probeResp.StatusCode}");

            var exitIp = (await http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(exitIp) || !IPAddress.TryParse(exitIp, out _))
                return (false, null, "api.ipify.org вернул некорректный IP");

            return (true, exitIp, "OK");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1500);
                }
            }
            catch
            {
                // Ignore process cleanup failure.
            }

            try
            {
                if (File.Exists(cfgPath))
                    File.Delete(cfgPath);
            }
            catch
            {
                // Ignore temp file cleanup failure.
            }
        }
    }

    private static async Task<(string PrivateKey, string PublicKey)> GenerateX25519KeypairAsync(CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AppSettings.XrayExePath,
                Arguments = "x25519",
                WorkingDirectory = AppSettings.BinDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!proc.Start())
            throw new InvalidOperationException("Не удалось запустить xray.exe x25519.");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"xray.exe x25519 завершился с ошибкой ({proc.ExitCode}): {stderr}");

        string? privUrl = null;
        string? pubUrl = null;

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("PrivateKey:", StringComparison.OrdinalIgnoreCase))
            {
                privUrl = line["PrivateKey:".Length..].Trim();
            }
            else if (line.StartsWith("Password (PublicKey):", StringComparison.OrdinalIgnoreCase))
            {
                pubUrl = line["Password (PublicKey):".Length..].Trim();
            }
            else if (line.StartsWith("PublicKey:", StringComparison.OrdinalIgnoreCase))
            {
                pubUrl = line["PublicKey:".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(privUrl) || string.IsNullOrWhiteSpace(pubUrl))
            throw new InvalidOperationException($"Не удалось разобрать вывод xray.exe x25519:\n{stdout}");

        return (Base64UrlToStandard(privUrl), Base64UrlToStandard(pubUrl));
    }

    private static async Task<WarpAccountData> RegisterAccountAsync(string privKey, string pubKey, CancellationToken ct)
    {
        var payload = new
        {
            key = pubKey,
            install_id = string.Empty,
            fcm_token = string.Empty,
            tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            model = "PC",
            serial_number = string.Empty,
            locale = "en_US"
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await RegHttp.PostAsync(CloudflareRegUrl, content, ct).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ошибка регистрации WARP ({(int)resp.StatusCode}): {respBody}");
        }

        using var doc = JsonDocument.Parse(respBody);
        var config = doc.RootElement.GetProperty("config");

        var clientIdB64 = config.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException("WARP API не вернул client_id.");
        var clientIdBytes = Convert.FromBase64String(clientIdB64);
        if (clientIdBytes.Length < 3)
            throw new InvalidOperationException("WARP client_id имеет неверную длину.");

        var peers = config.GetProperty("peers");
        if (peers.GetArrayLength() == 0)
            throw new InvalidOperationException("WARP API не вернул список peers.");

        var peerPublicKey = peers[0].GetProperty("public_key").GetString()
            ?? throw new InvalidOperationException("WARP API не вернул public_key пира.");

        var addresses = config.GetProperty("interface").GetProperty("addresses");
        var addrV4 = addresses.GetProperty("v4").GetString()
            ?? throw new InvalidOperationException("WARP API не вернул IPv4 адрес.");
        var addrV6 = addresses.GetProperty("v6").GetString()
            ?? throw new InvalidOperationException("WARP API не вернул IPv6 адрес.");

        return new WarpAccountData
        {
            PrivateKey = privKey,
            PublicKey = pubKey,
            PeerPublicKey = peerPublicKey,
            AddressV4 = addrV4,
            AddressV6 = addrV6,
            Reserved = [clientIdBytes[0], clientIdBytes[1], clientIdBytes[2]],
            Endpoint = WarpAccountData.DefaultEndpoint
        };
    }

    private static string Base64UrlToStandard(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0)
        {
            s += '=';
        }
        return s;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
