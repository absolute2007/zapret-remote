using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

/// <summary>
/// Generates VLESS share URIs, Happ routing deeplinks, and runtime Xray-core JSON configuration.
/// </summary>
public static class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] GoogleFastDnsDomains =
    [
        "domain:youtube.com",
        "domain:googlevideo.com",
        "domain:ytimg.com",
        "domain:googleapis.com",
        "domain:ggpht.com",
        "domain:youtu.be"
    ];

    public static string BuildVlessUri(string uuid, string lanIp, int port, string remark = "PC-Zapret")
    {
        var safeHost = string.IsNullOrWhiteSpace(lanIp) ? "127.0.0.1" : lanIp.Trim();
        var encodedRemark = Uri.EscapeDataString(remark);
        return $"vless://{uuid}@{safeHost}:{port}?encryption=none&security=none&type=tcp#{encodedRemark}";
    }

    public static string BuildHappRoutingDeeplink()
    {
        var profile = new
        {
            Name = "PCZapret",
            GlobalProxy = "true",
            RouteOrder = "block-direct-proxy",
            RemoteDNSType = "DoU",
            RemoteDNSDomain = string.Empty,
            RemoteDNSIP = "1.1.1.1",
            DomesticDNSType = "DoU",
            DomesticDNSDomain = string.Empty,
            DomesticDNSIP = "1.1.1.1",
            Geoipurl = string.Empty,
            Geositeurl = string.Empty,
            DomainStrategy = "IPIfNonMatch",
            FakeDNS = "true",
            UseChunkFiles = "true",
            DnsHosts = new { },
            DirectSites = Array.Empty<string>(),
            DirectIp = new[]
            {
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16",
                "224.0.0.0/4",
                "255.255.255.255"
            },
            ProxySites = Array.Empty<string>(),
            ProxyIp = Array.Empty<string>(),
            BlockSites = Array.Empty<string>(),
            BlockIp = new[]
            {
                "2000::/3"
            }
        };

        var json = JsonSerializer.Serialize(profile, CompactJsonOptions);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"happ://routing/onadd/{b64}";
    }

    public static string BuildRuntimeConfigJson(AppSettings settings, string? currentLanIp, TgWsProxyStatus? tgStatus)
    {
        var loopIps = new List<string> { "127.0.0.0/8" };
        if (!string.IsNullOrWhiteSpace(currentLanIp))
        {
            loopIps.Add(currentLanIp.Trim());
        }

        var bridgeEnabled = settings.EnableTgBridge &&
                            tgStatus is { IsReadyForBridge: true } &&
                            settings.TgBridgePort != settings.Port;

        var destOverride = settings.BlockQuic
            ? new[] { "http", "tls" }
            : new[] { "http", "tls", "quic" };

        var inbounds = new List<object>
        {
            new
            {
                tag = "lan-in",
                listen = "0.0.0.0",
                port = settings.Port,
                protocol = "vless",
                settings = new
                {
                    clients = new[]
                    {
                        new
                        {
                            id = settings.Uuid,
                            level = 0
                        }
                    },
                    decryption = "none"
                },
                streamSettings = new
                {
                    network = "tcp",
                    security = "none"
                },
                sniffing = new
                {
                    enabled = true,
                    destOverride,
                    routeOnly = false
                }
            }
        };

        if (bridgeEnabled && tgStatus is not null)
        {
            inbounds.Add(new
            {
                tag = "tgws-lan-in",
                listen = "0.0.0.0",
                port = settings.TgBridgePort,
                protocol = "dokodemo-door",
                settings = new
                {
                    address = tgStatus.LocalHost,
                    port = tgStatus.LocalPort,
                    network = "tcp"
                }
            });
        }

        var outbounds = new List<object>
        {
            new
            {
                tag = "direct",
                protocol = "freedom",
                settings = new { },
                streamSettings = new
                {
                    sockopt = new
                    {
                        domainStrategy = "UseIPv4",
                        tcpNoDelay = true,
                        tcpKeepAliveInterval = 15
                    }
                }
            },
            new
            {
                tag = "dns-out",
                protocol = "dns",
                settings = new
                {
                    rules = new object[]
                    {
                        new { action = "return", rCode = 0, qType = 28 },
                        new { action = "return", rCode = 0, qType = 65 }
                    }
                }
            },
            new
            {
                tag = "block",
                protocol = "blackhole",
                settings = new { }
            }
        };

        if (bridgeEnabled && tgStatus is not null)
        {
            var allowIps = new List<string> { "127.0.0.1" };
            if (!string.IsNullOrWhiteSpace(tgStatus.LocalHost) &&
                !string.Equals(tgStatus.LocalHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                allowIps.Add(tgStatus.LocalHost);
            }
            if (!string.IsNullOrWhiteSpace(currentLanIp))
            {
                allowIps.Add(currentLanIp.Trim());
            }

            outbounds.Add(new
            {
                tag = "tgws-out",
                protocol = "freedom",
                settings = new
                {
                    redirect = $"{tgStatus.LocalHost}:{tgStatus.LocalPort}",
                    finalRules = new object[]
                    {
                        new
                        {
                            action = "allow",
                            ip = allowIps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                        }
                    }
                }
            });
        }

        var rules = new List<object>
        {
            new
            {
                type = "field",
                inboundTag = new[] { "lan-in" },
                ip = loopIps.ToArray(),
                port = settings.Port.ToString(),
                outboundTag = "block"
            }
        };

        if (bridgeEnabled)
        {
            rules.Add(new
            {
                type = "field",
                inboundTag = new[] { "tgws-lan-in" },
                outboundTag = "tgws-out"
            });

            rules.Add(new
            {
                type = "field",
                inboundTag = new[] { "lan-in" },
                ip = loopIps.ToArray(),
                port = settings.TgBridgePort.ToString(),
                outboundTag = "tgws-out"
            });
        }

        rules.Add(new
        {
            type = "field",
            inboundTag = new[] { "lan-in" },
            port = "53",
            outboundTag = "dns-out"
        });

        if (settings.BlockQuic)
        {
            rules.Add(new
            {
                type = "field",
                inboundTag = new[] { "lan-in" },
                network = "udp",
                port = "443",
                outboundTag = "block"
            });
        }

        rules.Add(new
        {
            type = "field",
            inboundTag = new[] { "lan-in" },
            ip = new[] { "::/0" },
            outboundTag = "block"
        });

        rules.Add(new
        {
            type = "field",
            inboundTag = new[] { "lan-in" },
            outboundTag = "direct"
        });

        var dnsServers = new List<object>();
        var gatewayIp = NetworkDetector.GetGatewayIpv4(currentLanIp);
        if (!string.IsNullOrWhiteSpace(gatewayIp))
        {
            dnsServers.Add(new
            {
                address = gatewayIp,
                domains = GoogleFastDnsDomains
            });
        }
        dnsServers.Add("https+local://1.1.1.1/dns-query");
        dnsServers.Add("https+local://8.8.8.8/dns-query");
        dnsServers.Add("1.1.1.1");

        var config = new
        {
            log = new
            {
                loglevel = settings.VerboseLogging ? "info" : "warning"
            },
            dns = new
            {
                servers = dnsServers.ToArray(),
                queryStrategy = "UseIPv4"
            },
            policy = new
            {
                levels = new Dictionary<string, object>
                {
                    ["0"] = new
                    {
                        handshake = 4,
                        connIdle = 20,
                        uplinkOnly = 1,
                        downlinkOnly = 1
                    }
                }
            },
            inbounds = inbounds.ToArray(),
            outbounds = outbounds.ToArray(),
            routing = new
            {
                domainStrategy = "AsIs",
                rules = rules.ToArray()
            }
        };

        return JsonSerializer.Serialize(config, PrettyJsonOptions);
    }

    public static string WriteRuntimeConfig(AppSettings settings, string? currentLanIp, TgWsProxyStatus? tgStatus)
    {
        Directory.CreateDirectory(AppSettings.BinDirectory);
        var json = BuildRuntimeConfigJson(settings, currentLanIp, tgStatus);
        var tmpPath = AppSettings.RuntimeConfigPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, AppSettings.RuntimeConfigPath, overwrite: true);
        return AppSettings.RuntimeConfigPath;
    }
}
