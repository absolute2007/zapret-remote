using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZapretRemote.Models;
using ZapretRemote.Services;
using ZapretRemote.UI;

namespace ZapretRemote;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\ZapretRemote_SingleInstance_Mutex";
    private const string ActivateEventName = @"Local\ZapretRemote_Activate_Event";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunSelfTestAsync().GetAwaiter().GetResult();
        }

        if (args.Contains("--test-warp", StringComparer.OrdinalIgnoreCase))
        {
            return RunWarpTestAsync().GetAwaiter().GetResult();
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!isFirstInstance)
        {
            activateEvent.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var settings = AppSettings.Load();
        using var xrayManager = new XrayProcessManager();
        using var mainForm = new MainForm(settings, xrayManager);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { xrayManager.Dispose(); } catch { }
        };

        Application.ApplicationExit += (_, _) =>
        {
            try { xrayManager.Dispose(); } catch { }
        };

        var cts = new CancellationTokenSource();
        var watcherThread = new Thread(() =>
        {
            var handles = new WaitHandle[] { activateEvent, cts.Token.WaitHandle };
            while (!cts.IsCancellationRequested)
            {
                var idx = WaitHandle.WaitAny(handles);
                if (idx != 0 || mainForm.IsDisposed)
                    break;

                try
                {
                    mainForm.BeginInvoke(mainForm.RestoreFromTray);
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivateWatcher"
        };
        watcherThread.Start();

        try
        {
            Application.Run(mainForm);
        }
        finally
        {
            cts.Cancel();
            xrayManager.Dispose();
            cts.Dispose();
        }

        return 0;
    }

    private static async Task<int> RunSelfTestAsync()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            var settings = AppSettings.Load();
            Console.WriteLine($"Settings OK. UUID={settings.Uuid}, Port={settings.Port}, TgPort={settings.TgBridgePort}, BlockQuic={settings.BlockQuic}");

            var adapters = NetworkDetector.GetLanAdapters();
            var best = NetworkDetector.SelectBestAdapter(adapters, settings.PreferredIp);
            var gateway = NetworkDetector.GetGatewayIpv4(best?.IpAddress);
            Console.WriteLine($"Adapters={adapters.Count}, Selected={best?.DisplayName ?? "none"}, Gateway={gateway ?? "none"}");

            var routingDeeplink = XrayConfigBuilder.BuildHappRoutingDeeplink();
            Console.WriteLine($"Happ routing deeplink len={routingDeeplink.Length}");

            var zapret = ZapretMonitor.GetStatus();
            Console.WriteLine($"Zapret (read-only): Running={zapret.IsRunning}, Pids=[{string.Join(",", zapret.Pids)}]");

            var svcInfo = ZapretAutoFixService.InspectService();
            Console.WriteLine($"Zapret service: Exists={svcInfo.ServiceExists}, Preset={svcInfo.PresetName ?? "none"}, CanPatchGoogle={svcInfo.CanPatchGoogleRule}, HasBackup={svcInfo.HasBackup}");

            var tg = TgWsProxyService.GetStatus();
            Console.WriteLine($"TgWsProxy (read-only): Running={tg.IsProcessRunning}, Ready={tg.IsReadyForBridge}, Target={tg.LocalHost}:{tg.LocalPort}");

            var requiredPorts = settings.GetRequiredFirewallPorts(tg.IsReadyForBridge);
            var fwStatus = FirewallHelper.CheckRuleStatus(AppSettings.XrayExePath, requiredPorts);
            Console.WriteLine($"Firewall (read-only): {fwStatus} (ports: {string.Join(",", requiredPorts)})");

            var progress = new Progress<string>(msg => Console.WriteLine($"[Downloader] {msg}"));
            await XrayDownloader.EnsureXrayAsync(progress, CancellationToken.None);
            Console.WriteLine($"Xray present: {XrayDownloader.IsXrayInstalled()}");

            var uri = XrayConfigBuilder.BuildVlessUri(settings.Uuid, best?.IpAddress ?? "127.0.0.1", settings.Port);
            using (var qr = IconFactory.CreateQrBitmap(uri, 354))
            {
                Console.WriteLine($"Happ QR generated: {qr.Width}x{qr.Height}");
            }

            if (tg.IsReadyForBridge && !string.IsNullOrEmpty(tg.FullSecret))
            {
                var tgUri = TgWsProxyService.BuildTelegramProxyHttpsUri(best?.IpAddress ?? "127.0.0.1", settings.TgBridgePort, tg.FullSecret);
                using var tgQr = IconFactory.CreateQrBitmap(tgUri, 354);
                Console.WriteLine($"Telegram QR generated: {tgQr.Width}x{tgQr.Height}, URI={tgUri}");
            }

            using var manager = new XrayProcessManager();
            await manager.StartAsync(settings, best?.IpAddress, tg);
            Console.WriteLine($"Xray started: IsRunning={manager.IsRunning}, ActivePorts=[{string.Join(",", manager.ActivePorts)}]");

            foreach (var p in manager.ActivePorts)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, p);
                Console.WriteLine($"TCP port {p} reachable: {tcp.Connected}");
            }

            await VerifyEndToEndVlessTrafficAsync(settings.Uuid, settings.Port);

            manager.Stop();
            await Task.Delay(250);
            Console.WriteLine($"Xray stopped: IsRunning={manager.IsRunning}");

            var allClosed = true;
            foreach (var p in requiredPorts)
            {
                try
                {
                    using var tcpAfter = new TcpClient();
                    await tcpAfter.ConnectAsync(IPAddress.Loopback, p);
                    allClosed = false;
                }
                catch (SocketException)
                {
                    Console.WriteLine($"TCP port {p} closed after Stop: True");
                }
            }

            return allClosed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELF-TEST FAILED: {ex}");
            return 1;
        }
    }

    private static async Task VerifyEndToEndVlessTrafficAsync(string uuid, int serverPort)
    {
        var socksPort = GetFreeTcpPort();
        var clientConfigPath = Path.Combine(AppSettings.BinDirectory, "xray.selftest-client.json");

        var clientConfig = new
        {
            log = new { loglevel = "warning" },
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
                    tag = "proxy",
                    protocol = "vless",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = "127.0.0.1",
                                port = serverPort,
                                users = new[]
                                {
                                    new { id = uuid, encryption = "none" }
                                }
                            }
                        }
                    },
                    streamSettings = new
                    {
                        network = "tcp",
                        security = "none"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(clientConfigPath, JsonSerializer.Serialize(clientConfig));

        using var clientProc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppSettings.XrayExePath,
                Arguments = $"run -c \"{clientConfigPath}\"",
                WorkingDirectory = AppSettings.BinDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            }
        };

        try
        {
            clientProc.Start();
            await Task.Delay(500);

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync("https://cp.cloudflare.com/generate_204");
            Console.WriteLine($"End-to-end VLESS HTTPS test: {(int)resp.StatusCode} {resp.StatusCode}");
            if ((int)resp.StatusCode is not (204 or 200))
                throw new InvalidOperationException($"Unexpected HTTP status via VLESS: {resp.StatusCode}");
        }
        finally
        {
            try
            {
                if (!clientProc.HasExited)
                {
                    clientProc.Kill(entireProcessTree: true);
                    clientProc.WaitForExit(1500);
                }
            }
            catch { }
            try
            {
                if (File.Exists(clientConfigPath))
                    File.Delete(clientConfigPath);
            }
            catch { }
        }
    }

    private static async Task<int> RunWarpTestAsync()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            var settings = AppSettings.Load();
            var progress = new Progress<string>(msg => Console.WriteLine($"[WARP] {msg}"));
            await XrayDownloader.EnsureXrayAsync(progress, CancellationToken.None);

            var account = await WarpAccountService.EnsureAccountAsync(settings, progress, CancellationToken.None);
            var (endpoint, exitIp) = await WarpAccountService.FindWorkingEndpointAsync(account, progress, CancellationToken.None);
            account.Endpoint = endpoint;
            settings.Save();
            Console.WriteLine($"WARP account OK: v4={account.AddressV4}, endpoint={endpoint}, exitIp={exitIp}");

            var testSettings = new AppSettings
            {
                Uuid = settings.Uuid,
                Port = GetFreeTcpPort(),
                PreferredIp = settings.PreferredIp,
                BlockQuic = settings.BlockQuic,
                EnableTgBridge = false,
                TgBridgePort = GetFreeTcpPort(),
                VerboseLogging = settings.VerboseLogging,
                WarpMode = WarpRoutingMode.AllExceptRu,
                WarpAccount = account
            };

            var adapters = NetworkDetector.GetLanAdapters();
            var best = NetworkDetector.SelectBestAdapter(adapters, settings.PreferredIp);

            using var manager = new XrayProcessManager();
            try
            {
                await manager.StartAsync(testSettings, best?.IpAddress, tgStatus: null);
                await VerifyVlessWarpTikTokAsync(testSettings.Uuid, testSettings.Port);
            }
            finally
            {
                manager.Stop();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARP TEST FAILED: {ex}");
            return 1;
        }
    }

    private static async Task VerifyVlessWarpTikTokAsync(string uuid, int serverPort)
    {
        var socksPort = GetFreeTcpPort();
        var clientConfigPath = Path.Combine(AppSettings.BinDirectory, "xray.warptest-client.json");

        var clientConfig = new
        {
            log = new { loglevel = "warning" },
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
                    tag = "proxy",
                    protocol = "vless",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = "127.0.0.1",
                                port = serverPort,
                                users = new[]
                                {
                                    new { id = uuid, encryption = "none" }
                                }
                            }
                        }
                    },
                    streamSettings = new
                    {
                        network = "tcp",
                        security = "none"
                    }
                }
            }
        };

        await File.WriteAllTextAsync(clientConfigPath, JsonSerializer.Serialize(clientConfig));

        using var clientProc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppSettings.XrayExePath,
                Arguments = $"run -c \"{clientConfigPath}\"",
                WorkingDirectory = AppSettings.BinDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            }
        };

        try
        {
            clientProc.Start();
            await Task.Delay(500);

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var probeResp = await http.GetAsync("https://cp.cloudflare.com/generate_204");
            Console.WriteLine($"End-to-end VLESS -> WARP generate_204 test: {(int)probeResp.StatusCode} {probeResp.StatusCode}");
            if ((int)probeResp.StatusCode is not (204 or 200))
                throw new InvalidOperationException($"Unexpected HTTP status for generate_204 via WARP: {probeResp.StatusCode}");

            using var resp = await http.GetAsync("https://www.tiktok.com/");
            Console.WriteLine($"End-to-end VLESS -> WARP TikTok test: {(int)resp.StatusCode} {resp.StatusCode}");
            if ((int)resp.StatusCode >= 400)
                throw new InvalidOperationException($"Unexpected HTTP status for TikTok via WARP: {resp.StatusCode}");
        }
        finally
        {
            try
            {
                if (!clientProc.HasExited)
                {
                    clientProc.Kill(entireProcessTree: true);
                    clientProc.WaitForExit(1500);
                }
            }
            catch { }
            try
            {
                if (File.Exists(clientConfigPath))
                    File.Delete(clientConfigPath);
            }
            catch { }
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
