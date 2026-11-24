using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

public sealed record ZapretServiceInfo(
    bool ServiceExists,
    bool IsRunning,
    int[] Pids,
    string? PresetName,
    string? ImagePath,
    string? WinwsExePath,
    string? ListGooglePath,
    string? FakeGoogleBinPath,
    bool CanPatchGoogleRule,
    bool HasBackup,
    string? BackupCreatedAtUtc,
    string? BackupPresetName);

public sealed record ZapretDiagnosticReport(
    ZapretServiceInfo ServiceInfo,
    bool YouTubeApiOk,
    long YouTubeApiMs,
    string? YouTubeApiError,
    bool LocalCdnOk,
    string? LocalCdnHost,
    long LocalCdnMs,
    string? LocalCdnError,
    string? ExternalRedirectHost,
    string? ExternalRedirectIp,
    bool? ExternalCdnOk,
    long ExternalCdnMs,
    string? ExternalCdnError,
    bool RecommendAutoTune,
    bool IspBlocksExternalCdn,
    string SummaryText);

/// <summary>
/// Read-only inspection, live YouTube/CDN diagnostics, and safe backup-first auto-tuning
/// for only the Google/YouTube rule in an existing zapret Windows service.
/// </summary>
public static partial class ZapretAutoFixService
{
    private const string ZapretServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\zapret";
    private const string ZapretRegistryFullPath = @"HKEY_LOCAL_MACHINE\" + ZapretServiceKeyPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ZapretBackupData
    {
        public string OriginalImagePath { get; set; } = string.Empty;
        public string? OriginalPresetName { get; set; }
        public string SavedAtUtc { get; set; } = string.Empty;
    }

    public static ZapretServiceInfo InspectService()
    {
        var procStatus = ZapretMonitor.GetStatus();
        string? imagePath = null;
        string? presetName = null;
        var serviceExists = false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ZapretServiceKeyPath, writable: false);
            if (key is not null)
            {
                serviceExists = true;
                imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                presetName = key.GetValue("zapret-discord-youtube") as string;
            }
        }
        catch
        {
            serviceExists = false;
        }

        string? winwsExePath = null;
        string? listGooglePath = null;
        string? fakeGoogleBinPath = null;
        var canPatch = false;

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            winwsExePath = ExtractExecutablePath(imagePath);
            if (!string.IsNullOrWhiteSpace(winwsExePath))
            {
                var binDir = Path.GetDirectoryName(winwsExePath);
                if (!string.IsNullOrWhiteSpace(binDir))
                {
                    var candidateFake = Path.Combine(binDir, "tls_clienthello_www_google_com.bin");
                    if (File.Exists(candidateFake))
                        fakeGoogleBinPath = candidateFake;
                }
            }

            listGooglePath = ExtractListGooglePath(imagePath);
            canPatch = serviceExists &&
                       !string.IsNullOrWhiteSpace(winwsExePath) &&
                       !string.IsNullOrWhiteSpace(listGooglePath) &&
                       File.Exists(listGooglePath) &&
                       TrySplitGoogleSegment(imagePath, out _, out _, out _);
        }

        var backup = ReadBackup();

        return new ZapretServiceInfo(
            ServiceExists: serviceExists,
            IsRunning: procStatus.IsRunning,
            Pids: procStatus.Pids,
            PresetName: presetName,
            ImagePath: imagePath,
            WinwsExePath: winwsExePath,
            ListGooglePath: listGooglePath,
            FakeGoogleBinPath: fakeGoogleBinPath,
            CanPatchGoogleRule: canPatch,
            HasBackup: backup is not null && !string.IsNullOrWhiteSpace(backup.OriginalImagePath),
            BackupCreatedAtUtc: backup?.SavedAtUtc,
            BackupPresetName: backup?.OriginalPresetName);
    }

    public static bool EnsureBackupSaved(out string? errorMessage)
    {
        errorMessage = null;
        var info = InspectService();
        if (!info.ServiceExists || string.IsNullOrWhiteSpace(info.ImagePath))
        {
            errorMessage = "Служба Windows 'zapret' не найдена в системе.";
            return false;
        }

        var existing = ReadBackup();
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.OriginalImagePath))
            return true;

        try
        {
            var backup = new ZapretBackupData
            {
                OriginalImagePath = info.ImagePath,
                OriginalPresetName = info.PresetName,
                SavedAtUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(AppSettings.ZapretBackupPath, JsonSerializer.Serialize(backup, JsonOptions), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Не удалось сохранить резервную копию zapret-backup.json: {ex.Message}";
            return false;
        }
    }

    public static async Task<ZapretDiagnosticReport> DiagnoseYouTubeAsync(string xrayExePath, CancellationToken ct)
    {
        var info = InspectService();
        if (!File.Exists(xrayExePath))
        {
            return new ZapretDiagnosticReport(
                info, false, 0, "xray.exe ещё не загружен.",
                false, null, 0, null, null, null, null, 0, null,
                false, false,
                "Файл xray.exe отсутствует в папке bin. Сначала запустите прокси-мост или докачайте xray.exe.");
        }

        var httpPort = FindFreeLoopbackPort();
        var tmpCfg = Path.Combine(Path.GetTempPath(), $"zapret_remote_diag_{Guid.NewGuid():N}.json");
        var utf8NoBom = new UTF8Encoding(false);
        var cfgJson = $$"""
            {
              "log": { "loglevel": "warning" },
              "inbounds": [{ "listen": "127.0.0.1", "port": {{httpPort}}, "protocol": "http" }],
              "outbounds": [{ "protocol": "freedom", "settings": { "domainStrategy": "UseIPv4" } }]
            }
            """;
        File.WriteAllText(tmpCfg, cfgJson, utf8NoBom);

        using var job = new JobObject();
        Process? proc = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = xrayExePath,
                Arguments = $"run -c \"{tmpCfg}\"",
                WorkingDirectory = Path.GetDirectoryName(xrayExePath) ?? AppSettings.BinDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            proc = Process.Start(psi);
            if (proc is null)
                throw new InvalidOperationException("Не удалось запустить временный xray.exe для диагностики.");

            job.AssignProcess(proc.Handle);
            await Task.Delay(500, ct);

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
                UseProxy = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            var ytOk = false;
            long ytMs = 0;
            string? ytErr = null;
            var urlsToProbe = new List<(int Itag, string Url)>();

            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
                {
                    Content = new StringContent(
                        """{"videoId":"OPf0YbXqDm0","context":{"client":{"clientName":"IOS","clientVersion":"20.10.4","hl":"ru","gl":"RU"}}}""",
                        Encoding.UTF8,
                        "application/json")
                };
                req.Headers.UserAgent.ParseAdd("com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3 like Mac OS X)");
                using var resp = await client.SendAsync(req, ct);
                ytMs = sw.ElapsedMilliseconds;
                var json = await resp.Content.ReadAsStringAsync(ct);
                ytOk = resp.IsSuccessStatusCode;
                if (!ytOk)
                {
                    ytErr = $"HTTP {(int)resp.StatusCode}";
                }
                else
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("streamingData", out var sd))
                    {
                        foreach (var prop in new[] { "adaptiveFormats", "formats" })
                        {
                            if (!sd.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                                continue;
                            foreach (var item in arr.EnumerateArray())
                            {
                                if (item.TryGetProperty("url", out var uProp) &&
                                    uProp.GetString() is { Length: > 0 } u &&
                                    item.TryGetProperty("itag", out var itagProp) &&
                                    itagProp.TryGetInt32(out var itag) &&
                                    itag is 399 or 137 or 18 or 140)
                                {
                                    urlsToProbe.Add((itag, u));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ytMs = sw.ElapsedMilliseconds;
                ytErr = ShortError(ex);
            }

            var localCdnOk = false;
            string? localHost = null;
            long localMs = 0;
            string? localErr = null;

            string? extHost = null;
            string? extIp = null;
            bool? extOk = null;
            long extMs = 0;
            string? extErr = null;

            string? redirectUrl = null;

            foreach (var (_, streamUrl) in urlsToProbe.OrderBy(x => x.Itag == 399 ? 0 : 1))
            {
                if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
                    continue;

                localHost ??= uri.Host;
                var cdnSw = Stopwatch.StartNew();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl + "&range=0-65535");
                    req.Headers.UserAgent.ParseAdd("com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3 like Mac OS X)");
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                    {
                        redirectUrl ??= loc.ToString();
                        extHost ??= loc.Host;
                        continue;
                    }

                    if (resp.IsSuccessStatusCode)
                    {
                        _ = await resp.Content.ReadAsByteArrayAsync(ct);
                        localMs = cdnSw.ElapsedMilliseconds;
                        localHost = uri.Host;
                        localCdnOk = true;
                        if (redirectUrl is not null)
                            break;
                    }
                    else
                    {
                        localErr ??= $"HTTP {(int)resp.StatusCode}";
                    }
                }
                catch (Exception ex)
                {
                    localMs = cdnSw.ElapsedMilliseconds;
                    localErr ??= ShortError(ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(redirectUrl) && Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redUri))
            {
                extHost = redUri.Host;
                extIp = await ResolveViaDohAsync(client, extHost, ct);

                var extSw = Stopwatch.StartNew();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                    req.Headers.UserAgent.ParseAdd("com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3 like Mac OS X)");
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    _ = await resp.Content.ReadAsByteArrayAsync(ct);
                    extMs = extSw.ElapsedMilliseconds;
                    extOk = resp.IsSuccessStatusCode;
                    if (!resp.IsSuccessStatusCode)
                        extErr = $"HTTP {(int)resp.StatusCode}";
                }
                catch (Exception ex)
                {
                    extMs = extSw.ElapsedMilliseconds;
                    extOk = false;
                    extErr = ShortError(ex);
                }
            }

            var recommendTune = (!ytOk || !localCdnOk) || (extOk == false && info.CanPatchGoogleRule);
            var ispBlockedExternal = ytOk && localCdnOk && extOk == false;

            var summary = BuildDiagnosticSummary(
                info, ytOk, ytMs, ytErr,
                localCdnOk, localHost, localMs, localErr,
                extHost, extIp, extOk, extMs, extErr);

            return new ZapretDiagnosticReport(
                info, ytOk, ytMs, ytErr,
                localCdnOk, localHost, localMs, localErr,
                extHost, extIp, extOk, extMs, extErr,
                recommendTune, ispBlockedExternal, summary);
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            proc?.Dispose();
            try { File.Delete(tmpCfg); } catch { }
        }
    }

    public static bool AutoTuneAndApply(string xrayExePath, out string reportText, out string? errorMessage)
    {
        reportText = string.Empty;
        errorMessage = null;

        var info = InspectService();
        if (!info.CanPatchGoogleRule ||
            string.IsNullOrWhiteSpace(info.ImagePath) ||
            string.IsNullOrWhiteSpace(info.WinwsExePath) ||
            string.IsNullOrWhiteSpace(info.ListGooglePath))
        {
            errorMessage = "Не найдена служба Windows 'zapret' или правило list-google.txt в её параметрах.";
            return false;
        }

        if (!EnsureBackupSaved(out errorMessage))
            return false;

        if (!TrySplitGoogleSegment(info.ImagePath, out var beforeGoogle, out var currentGoogleSeg, out var afterGoogle))
        {
            errorMessage = "Не удалось безопасно выделить сегмент list-google.txt в параметрах службы zapret.";
            return false;
        }

        var binDir = Path.GetDirectoryName(info.WinwsExePath) ?? string.Empty;
        var fakeGoogle = info.FakeGoogleBinPath ?? Path.Combine(binDir, "tls_clienthello_www_google_com.bin");
        var hasFakeBin = File.Exists(fakeGoogle);
        var listQ = $"\"{info.ListGooglePath}\"";
        var fakeQ = $"\"{fakeGoogle}\"";
        var fakeMod = hasFakeBin ? $"--dpi-desync-fake-tls={fakeQ}" : "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com";
        var fakeSeq = hasFakeBin ? $"--dpi-desync-split-seqovl-pattern={fakeQ} {fakeMod}" : fakeMod;

        var candidates = new (string Name, string Segment)[]
        {
            ("Текущая стратегия", currentGoogleSeg),
            ("ALT11 (fake,multisplit ts)", $"--filter-tcp=443 --hostlist={listQ} --ip-id=zero --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 {fakeSeq}"),
            ("BADSUM (fake,multisplit)", $"--filter-tcp=443 --hostlist={listQ} --ip-id=zero --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-fooling=badsum --dpi-desync-repeats=8 {fakeMod}"),
            ("BADSEQ (fake,multisplit)", $"--filter-tcp=443 --hostlist={listQ} --ip-id=zero --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-repeats=8 {fakeSeq}"),
            ("MD5SIG (fake,multisplit)", $"--filter-tcp=443 --hostlist={listQ} --ip-id=zero --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-fooling=md5sig --dpi-desync-repeats=6 {fakeMod}"),
            ("DISORDER2 (sniext)", $"--filter-tcp=443 --hostlist={listQ} --ip-id=zero --dpi-desync=disorder2 --dpi-desync-split-pos=1,sniext+1,midsld")
        };

        var port = FindFreeLoopbackPort();
        var stamp = Guid.NewGuid().ToString("N");
        var psPath = Path.Combine(Path.GetTempPath(), $"zapret_remote_tune_{stamp}.ps1");
        var resPath = Path.Combine(Path.GetTempPath(), $"zapret_remote_tune_{stamp}.txt");
        var cfgPath = Path.Combine(Path.GetTempPath(), $"zapret_remote_tune_{stamp}.json");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine($"$xrayExe = '{PsEscape(xrayExePath)}'");
            sb.AppendLine($"$cfgPath = '{PsEscape(cfgPath)}'");
            sb.AppendLine($"$resPath = '{PsEscape(resPath)}'");
            sb.AppendLine($"$winwsExe = '{PsEscape(info.WinwsExePath)}'");
            sb.AppendLine($"$binDir = '{PsEscape(binDir)}'");
            sb.AppendLine($"$origImagePath = '{PsEscape(info.ImagePath)}'");
            sb.AppendLine($"$beforeGoogle = '{PsEscape(beforeGoogle)}'");
            sb.AppendLine($"$afterGoogle = '{PsEscape(afterGoogle)}'");
            sb.AppendLine("$utf8 = New-Object System.Text.UTF8Encoding($false)");
            sb.AppendLine("[IO.File]::WriteAllText($cfgPath, '{\"log\":{\"loglevel\":\"warning\"},\"inbounds\":[{\"listen\":\"127.0.0.1\",\"port\":" + port + ",\"protocol\":\"socks\"}],\"outbounds\":[{\"protocol\":\"freedom\",\"settings\":{\"domainStrategy\":\"UseIPv4\"}}]}', $utf8)");
            sb.AppendLine("$cands = @(");
            for (var i = 0; i < candidates.Length; i++)
            {
                var comma = i < candidates.Length - 1 ? "," : "";
                sb.AppendLine($"  @{{ Name = '{PsEscape(candidates[i].Name)}'; Seg = '{PsEscape(candidates[i].Segment)}' }}{comma}");
            }
            sb.AppendLine(")");
            sb.AppendLine($$"""
                $zapretWasRunning = ((Get-Service zapret -ErrorAction SilentlyContinue).Status -eq 'Running')
                $xp = $null
                $lines = New-Object System.Collections.Generic.List[string]
                $bestScore = -1
                $bestMs = 999999
                $bestIndex = 0
                $appliedImagePath = $origImagePath

                try {
                    if ($zapretWasRunning) {
                        Stop-Service zapret -Force
                        Start-Sleep -Milliseconds 400
                    }
                    Get-Process winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 250

                    $xp = Start-Process -FilePath $xrayExe -ArgumentList "run -c `"$cfgPath`"" -PassThru -WindowStyle Hidden
                    Start-Sleep -Milliseconds 500

                    # Resolve a local and external googlevideo target if available
                    $targets = @(
                        @{ Tag = 'YT'; Url = 'https://www.youtube.com/generate_204' },
                        @{ Tag = 'CDN1'; Url = 'https://rr2---sn-gvnuxaxjvh-nbjl.googlevideo.com/generate_204' },
                        @{ Tag = 'CDN2'; Url = 'https://rr4---sn-aj5goc-5x.googlevideo.com/generate_204' }
                    )

                    for ($i = 0; $i -lt $cands.Count; $i++) {
                        $c = $cands[$i]
                        $wArgs = "--wf-tcp=443 $($c.Seg)"
                        $w = Start-Process -FilePath $winwsExe -ArgumentList $wArgs -WorkingDirectory $binDir -PassThru -WindowStyle Hidden
                        Start-Sleep -Milliseconds 300

                        $score = 0
                        $totalMs = 0
                        $parts = @()
                        $oldPref = $ErrorActionPreference
                        $ErrorActionPreference = 'Continue'
                        foreach ($t in $targets) {
                            $sw = [Diagnostics.Stopwatch]::StartNew()
                            $null = & curl.exe -sS -k -o NUL --max-time 2.5 --socks5-hostname 127.0.0.1:{{port}} $t.Url 2>&1
                            if ($LASTEXITCODE -eq 0) {
                                $score += if ($t.Tag -eq 'CDN2') { 2 } else { 1 }
                                $ms = $sw.ElapsedMilliseconds
                                $totalMs += $ms
                                $parts += "$($t.Tag)=OK(${ms}ms)"
                            } else {
                                $parts += "$($t.Tag)=FAIL"
                            }
                        }
                        $ErrorActionPreference = $oldPref

                        if (-not $w.HasExited) { Stop-Process -Id $w.Id -Force -ErrorAction SilentlyContinue }
                        Start-Sleep -Milliseconds 150

                        $lines.Add("$($c.Name): score=$score ($($parts -join ', '))")
                        if ($score -gt $bestScore -or ($score -eq $bestScore -and $totalMs -lt $bestMs -and $i -eq 0)) {
                            $bestScore = $score
                            $bestMs = $totalMs
                            $bestIndex = $i
                        }
                    }

                    if ($bestIndex -gt 0 -and $bestScore -gt 0) {
                        $appliedImagePath = ($beforeGoogle + ' ' + $cands[$bestIndex].Seg + ' ' + $afterGoogle).Trim()
                        [Microsoft.Win32.Registry]::SetValue('{{ZapretRegistryFullPath}}', 'ImagePath', $appliedImagePath, [Microsoft.Win32.RegistryValueKind]::ExpandString)
                        $lines.Add("APPLIED=$($cands[$bestIndex].Name)")
                    } else {
                        $lines.Add('APPLIED=NONE (оригинальная стратегия сохранена без изменений)')
                    }
                    [IO.File]::WriteAllLines($resPath, $lines, $utf8)
                }
                finally {
                    if ($xp -and -not $xp.HasExited) { Stop-Process -Id $xp.Id -Force -ErrorAction SilentlyContinue }
                    Get-Process winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                    Remove-Item $cfgPath -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 200
                    if ($zapretWasRunning) { Start-Service zapret -ErrorAction SilentlyContinue }
                }
                """);

            File.WriteAllText(psPath, sb.ToString(), new UTF8Encoding(false));
            var ok = FirewallHelper.RunElevated(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{psPath}\"",
                out errorMessage,
                timeoutMs: 90000,
                contextName: "автоподбора стратегии zapret");

            if (!ok)
                return false;

            reportText = File.Exists(resPath)
                ? File.ReadAllText(resPath, Encoding.UTF8)
                : "Подбор завершён.";
            return true;
        }
        finally
        {
            try { File.Delete(psPath); } catch { }
            try { File.Delete(resPath); } catch { }
            try { File.Delete(cfgPath); } catch { }
        }
    }

    public static bool RestoreOriginal(out string? errorMessage)
    {
        errorMessage = null;
        var backup = ReadBackup();
        if (backup is null || string.IsNullOrWhiteSpace(backup.OriginalImagePath))
        {
            errorMessage = "Файл резервной копии zapret-backup.json не найден.";
            return false;
        }

        var script =
            $"$ErrorActionPreference = 'Stop'; " +
            $"$orig = '{PsEscape(backup.OriginalImagePath)}'; " +
            $"if ((Get-Service zapret -ErrorAction SilentlyContinue).Status -eq 'Running') {{ Stop-Service zapret -Force; Start-Sleep -Milliseconds 300 }}; " +
            $"Get-Process winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; " +
            $"[Microsoft.Win32.Registry]::SetValue('{ZapretRegistryFullPath}', 'ImagePath', $orig, [Microsoft.Win32.RegistryValueKind]::ExpandString); " +
            $"Start-Service zapret";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return FirewallHelper.RunElevated(
            "powershell.exe",
            $"-NoProfile -EncodedCommand {encoded}",
            out errorMessage,
            timeoutMs: 30000,
            contextName: "отката службы zapret");
    }

    private static ZapretBackupData? ReadBackup()
    {
        try
        {
            if (!File.Exists(AppSettings.ZapretBackupPath))
                return null;
            var json = File.ReadAllText(AppSettings.ZapretBackupPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<ZapretBackupData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ResolveViaDohAsync(HttpClient client, string host, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("Answer", out var ans) || ans.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in ans.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                    item.TryGetProperty("data", out var d))
                {
                    return d.GetString();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string BuildDiagnosticSummary(
        ZapretServiceInfo info,
        bool ytOk, long ytMs, string? ytErr,
        bool localCdnOk, string? localHost, long localMs, string? localErr,
        string? extHost, string? extIp, bool? extOk, long extMs, string? extErr)
    {
        var sb = new StringBuilder();
        sb.AppendLine(info.IsRunning
            ? $"Служба zapret: активна (PID {string.Join(", ", info.Pids)}, профиль: {info.PresetName ?? "неизвестен"})"
            : "Служба zapret: НЕ ЗАПУЩЕНА");
        sb.AppendLine(info.HasBackup
            ? $"Резервная копия: сохранена ({info.BackupCreatedAtUtc})"
            : "Резервная копия: ещё не создавалась (оригинал zapret не изменялся)");
        sb.AppendLine();
        sb.AppendLine($"1. YouTube API (www.youtube.com): {(ytOk ? $"OK ({ytMs} мс)" : $"ОШИБКА ({ytErr})")}");
        sb.AppendLine($"2. Локальный CDN ({localHost ?? "не найден"}): {(localCdnOk ? $"OK ({localMs} мс)" : $"ОШИБКА ({localErr ?? "нет ответа"})")}");

        if (!string.IsNullOrWhiteSpace(extHost))
        {
            var extState = extOk == true
                ? $"OK ({extMs} мс)"
                : $"ТАЙМАУТ / СБРОС ({extErr ?? "нет ответа"})";
            sb.AppendLine($"3. Внешний 302-CDN ({extHost} [{extIp ?? "?"}]): {extState}");
        }
        else
        {
            sb.AppendLine("3. Внешний 302-CDN: редирект не встретился (видео отдаётся напрямую локальным узлом).");
        }

        sb.AppendLine();
        if (ytOk && localCdnOk && extOk == false)
        {
            sb.AppendLine("ИТОГ: Ваш zapret исправно работает с YouTube и локальным кешем (поэтому на ПК в браузере ролики грузятся сразу). Но для части H.264/iOS-потоков YouTube делает 302-редирект на внешний узел (74.125.* / 173.194.*), который режется провайдером по IP/TLS на уровне ТСПУ — локальный zapret физически не может обойти IP-блокировку без внешнего сервера.");
        }
        else if (!ytOk || !localCdnOk)
        {
            sb.AppendLine("ИТОГ: Текущий zapret не пробивает даже базовый YouTube / локальный CDN. Нажмите «Автоподбор zapret (UAC)», чтобы безопасно протестировать и применить рабочую стратегию только для правила list-google.txt (с сохранением бэкапа оригинала).");
        }
        else
        {
            sb.AppendLine("ИТОГ: Всё работает чисто, YouTube и CDN отвечают без задержек.");
        }

        return sb.ToString();
    }

    private static string? ExtractExecutablePath(string imagePath)
    {
        var trimmed = imagePath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
                return trimmed[1..end];
        }
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
    }

    private static string? ExtractListGooglePath(string imagePath)
    {
        var match = ListGoogleRegex().Match(imagePath);
        return match.Success ? match.Groups[1].Value.Trim('"') : null;
    }

    private static bool TrySplitGoogleSegment(string imagePath, out string beforeGoogle, out string googleSegment, out string afterGoogle)
    {
        beforeGoogle = string.Empty;
        googleSegment = string.Empty;
        afterGoogle = string.Empty;

        var exeEnd = imagePath.StartsWith('"')
            ? imagePath.IndexOf('"', 1) + 1
            : imagePath.IndexOf(' ');
        if (exeEnd <= 0 || exeEnd >= imagePath.Length)
            return false;

        var exePrefix = imagePath[..exeEnd].Trim();
        var argsPart = imagePath[exeEnd..];
        var parts = Regex.Split(argsPart, @"\s+--new\s+", RegexOptions.IgnoreCase);

        var idx = Array.FindIndex(parts, p =>
            p.Contains("list-google.txt", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("--filter-tcp", StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;

        googleSegment = parts[idx].Trim();
        beforeGoogle = idx == 0 ? exePrefix : $"{exePrefix} {string.Join(" --new ", parts[..idx].Select(p => p.Trim()))} --new";

        if (idx + 1 < parts.Length)
        {
            afterGoogle = "--new " + string.Join(" --new ", parts[(idx + 1)..].Select(p => p.Trim()));
        }

        return true;
    }

    private static int FindFreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string PsEscape(string value) => value.Replace("'", "''");

    private static string ShortError(Exception ex)
    {
        if (ex is TaskCanceledException)
            return "таймаут ожидания ответа";
        if (ex.InnerException is not null)
            return ex.InnerException.Message;
        return ex.Message;
    }

    [GeneratedRegex(@"--hostlist(?:=|\s+)(""[^""]*list-google\.txt""|[^\s]*list-google\.txt)", RegexOptions.IgnoreCase)]
    private static partial Regex ListGoogleRegex();
}
