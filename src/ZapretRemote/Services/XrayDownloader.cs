using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

/// <summary>
/// Downloads and extracts the official XTLS/Xray-core Windows x64 binary if missing.
/// </summary>
public static class XrayDownloader
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
    private const string DirectLatestZipUrl = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretRemote", "1.0"));
        return client;
    }

    public static bool IsXrayInstalled() => File.Exists(AppSettings.XrayExePath);

    public static async Task EnsureXrayAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsXrayInstalled())
            return;

        Directory.CreateDirectory(AppSettings.BinDirectory);
        var tempZipPath = Path.Combine(AppSettings.BinDirectory, "xray-download.tmp.zip");

        try
        {
            progress?.Report("Поиск последнего релиза Xray-core...");
            var downloadUrl = await ResolveDownloadUrlAsync(ct).ConfigureAwait(false);

            progress?.Report("Скачивание Xray-windows-64.zip...");
            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dest = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int lastPercent = -1;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;

                    if (totalBytes is > 0)
                    {
                        var percent = (int)(totalRead * 100 / totalBytes.Value);
                        if (percent != lastPercent && (percent % 5 == 0 || percent == 100))
                        {
                            lastPercent = percent;
                            progress?.Report($"Скачивание Xray-core: {percent}%");
                        }
                    }
                    else
                    {
                        progress?.Report($"Скачивание Xray-core: {totalRead / (1024 * 1024.0):F1} МБ");
                    }
                }
            }

            progress?.Report("Распаковка xray.exe...");
            ExtractArchive(tempZipPath, AppSettings.BinDirectory);

            if (!File.Exists(AppSettings.XrayExePath))
                throw new InvalidOperationException("Архив Xray-core не содержит xray.exe.");
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
            }
            catch
            {
                // Ignore cleanup failure of temp archive.
            }
        }
    }

    private static async Task<string> ResolveDownloadUrlAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return DirectLatestZipUrl;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.Equals(name, "Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            return url;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fallback to GitHub's direct latest release redirect.
        }

        return DirectLatestZipUrl;
    }

    private static void ExtractArchive(string zipPath, string binDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            if (!entry.Name.Equals("xray.exe", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.Equals("geoip.dat", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.Equals("geosite.dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = Path.Combine(binDir, entry.Name);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }
}
