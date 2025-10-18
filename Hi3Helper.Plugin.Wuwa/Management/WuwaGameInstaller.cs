using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

[GeneratedComClass]
partial class WuwaGameInstaller : GameInstallerBase
{
    private const double ExCacheDurationInMinute = 10d;

    private DateTimeOffset _cacheExpiredUntil = DateTimeOffset.MinValue;
    private WuwaApiResponseResourceIndex? _currentIndex;

    private string? GameAssetBaseUrl => (GameManager as WuwaGameManager)?.GameResourceBaseUrl;

    private readonly HttpClient _downloadHttpClient;
	internal WuwaGameInstaller(IGameManager? gameManager) : base(gameManager)
	{
        _downloadHttpClient = new PluginHttpClientBuilder()
			.SetAllowedDecompression(DecompressionMethods.GZip)
			.AllowCookies()
			.AllowRedirections()
			.AllowUntrustedCert()
			.Create();
	}

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if (GameAssetBaseUrl is null)
            return 0L;

        // Ensure API/init is ready
        await InitAsync(token).ConfigureAwait(false);

        return gameInstallerKind switch
        {
            GameInstallerKind.None => 0L,
            GameInstallerKind.Install or GameInstallerKind.Update or GameInstallerKind.Preload =>
                await CalculateDownloadedBytesAsync(token).ConfigureAwait(false),
            _ => 0L,
        };
	}

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if (GameAssetBaseUrl is null)
            return 0L;

        // Ensure API/init is ready
        await InitAsync(token).ConfigureAwait(false);

        // Load index (cached)
        var index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);
        if (index?.Resource == null || index.Resource.Length == 0)
            return 0L;

        try
        {
            // Sum sizes; clamp to long.MaxValue to avoid overflow
            ulong total = 0;
            foreach (var r in index.Resource)
            {
                if (r?.Size != null)
                    total = unchecked(total + r.Size);
            }

            return total > (ulong)long.MaxValue ? long.MaxValue : (long)total;
        }
        catch
        {
            return 0L;
        }
    }

    /// <summary>
    /// Start install: downloads indexFile.json, iterates entries and downloads each resource.
    /// Supports chunked resources via HTTP Range requests when chunkInfos are present.
    /// Writes files under the current install path (GameManager.SetGamePath expected to be set).
    /// Progress delegates are available but not invoked to avoid signature assumptions in this snippet.
    /// </summary>
    protected override async Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        if (GameAssetBaseUrl is null)
            throw new InvalidOperationException("Game asset base URL is not initialized.");

        // Ensure initialization (loads API/game config)
        await InitAsync(token).ConfigureAwait(false);

        // Download index JSON (use cached)
        WuwaApiResponseResourceIndex? index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);
        if (index?.Resource == null || index.Resource.Length == 0)
            throw new InvalidOperationException("Resource index is empty.");

        string? installPath = null;
        GameManager.GetGamePath(out installPath);

        if (string.IsNullOrEmpty(installPath))
            throw new InvalidOperationException("Game install path isn't set.");

        // Base URI for resources: GameAssetBaseUrl typically ends with indexFile.json
        Uri baseUri = new(GameAssetBaseUrl, UriKind.Absolute);
        string baseDirectory = baseUri.GetLeftPart(UriPartial.Path);
        // remove the index file part to get the directory
        int lastSlash = baseDirectory.LastIndexOf('/');
        if (lastSlash >= 0)
            baseDirectory = baseDirectory.Substring(0, lastSlash + 1);

        long totalBytesToDownload = 0;
        foreach (var r in index.Resource)
            totalBytesToDownload += (long)(r?.Size ?? 0);

        long downloadedBytes = 0;

        foreach (var entry in index.Resource)
        {
            token.ThrowIfCancellationRequested();

            if (entry == null || string.IsNullOrEmpty(entry.Dest))
                continue;

            string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
            string outputPath = Path.Combine(installPath, relativePath);
            string? parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            // If file exists and md5 matches, skip download
            if (File.Exists(outputPath) && !string.IsNullOrEmpty(entry.Md5))
            {
                try
                {
                    using var fs = File.OpenRead(outputPath);
                    string currentMd5 = ComputeMD5Hex(fs);
                    if (string.Equals(currentMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadedBytes += (long)entry.Size;
                        continue;
                    }
                }
                catch
                {
                    // fallback to re-download
                }
            }

            // Download either as whole file or by chunks
            if (entry.ChunkInfos == null || entry.ChunkInfos.Length == 0)
            {
                // whole file
                Uri fileUri = new Uri(new Uri(baseDirectory), entry.Dest);
                await DownloadWholeFileAsync(fileUri, outputPath, token).ConfigureAwait(false);
            }
            else
            {
                // chunked: stream into a temp file and append chunks
                Uri fileUri = new Uri(new Uri(baseDirectory), entry.Dest);
                await DownloadChunkedFileAsync(fileUri, outputPath, entry.ChunkInfos, token).ConfigureAwait(false);
            }

            // Verify MD5 if provided
            if (!string.IsNullOrEmpty(entry.Md5))
            {
                using var fsVerify = File.OpenRead(outputPath);
                string md5 = ComputeMD5Hex(fsVerify);
                if (!string.Equals(md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"MD5 mismatch for {entry.Dest}: expected {entry.Md5}, got {md5}");
            }

            downloadedBytes += (long)entry.Size;

            // Note: Progress delegates exist but their signatures are not referenced here to avoid breaking changes.
            // If you want progress reporting, call progressDelegate / progressStateDelegate here with appropriate signature.
        }
    }

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // For preload, reuse install routine for now (could filter resources)
        return StartInstallAsyncInner(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // For update, reuse install routine (will overwrite or skip existing files)
        return StartInstallAsyncInner(progressDelegate, progressStateDelegate, token);
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public override void Dispose()
	{
		_downloadHttpClient.Dispose();
		GC.SuppressFinalize(this);
	}

    // ---------- Helpers ----------
    private static string ComputeMD5Hex(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private async Task<WuwaApiResponseResourceIndex?> DownloadResourceIndexAsync(string indexUrl, CancellationToken token)
    {
        // Try the provided URL, but be tolerant to 404 by attempting a few reasonable fallbacks.
        // Also enable number handling so numeric fields that are strings deserialize correctly.
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Candidate URL generator: original + some common alternates
        string[] candidates = BuildIndexUrlCandidates(indexUrl);

        HttpRequestException? lastHttpEx = null;
        foreach (string candidate in candidates)
        {
            try
            {
                using HttpResponseMessage resp = await _downloadHttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    // Non-success codes: treat 404 as "try next candidate", otherwise bubble
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        continue;

                    resp.EnsureSuccessStatusCode(); // will throw
                }

                using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<WuwaApiResponseResourceIndex>(stream, options, token).ConfigureAwait(false);
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.NotFound)
            {
                // try next candidate
                lastHttpEx = httpEx;
                continue;
            }
            catch (HttpRequestException httpEx)
            {
                // Other HTTP errors — remember and break so caller can see meaningful error
                lastHttpEx = httpEx;
                break;
            }
        }

        // If we exhausted candidates and only saw 404s, return null (caller treats empty index defensively).
        // If we saw another HTTP error, rethrow the last one to surface the problem.
        if (lastHttpEx != null && lastHttpEx.StatusCode != HttpStatusCode.NotFound)
            throw lastHttpEx;

        return null;
    }

    private static string[] BuildIndexUrlCandidates(string indexUrl)
    {
        if (string.IsNullOrEmpty(indexUrl))
            return Array.Empty<string>();

        try
        {
            var uri = new Uri(indexUrl, UriKind.RelativeOrAbsolute);
            if (!uri.IsAbsoluteUri)
            {
                // Best-effort: add https scheme if missing
                uri = new Uri("https://" + indexUrl.TrimStart('/'));
            }

            string path = uri.AbsolutePath ?? string.Empty;
            string dir = path;
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0)
                dir = path.Substring(0, lastSlash + 1);
            string hostAndScheme = uri.GetLeftPart(UriPartial.Authority);

            var candidates = new[]
            {
                uri.ToString(), // original
                hostAndScheme + dir + "index.json",       // try index.json
                hostAndScheme + dir + "indexFile.json",   // try indexFile.json explicitly
                hostAndScheme + dir,                      // directory (server may redirect)
            }.Distinct().ToArray();

            return candidates;
        }
        catch
        {
            return new[] { indexUrl };
        }
    }

    private async Task<WuwaApiResponseResourceIndex?> GetCachedIndexAsync(bool force, CancellationToken token)
    {
        if (!force && _currentIndex != null && DateTimeOffset.UtcNow <= _cacheExpiredUntil)
            return _currentIndex;

        if (GameAssetBaseUrl is null)
            throw new InvalidOperationException("Game asset base URL is not initialized.");

        _currentIndex = await DownloadResourceIndexAsync(GameAssetBaseUrl, token).ConfigureAwait(false);
        UpdateCacheExpiration();
        return _currentIndex;
    }

    private void UpdateCacheExpiration() => _cacheExpiredUntil = DateTimeOffset.UtcNow.AddMinutes(ExCacheDurationInMinute);

    private async Task DownloadWholeFileAsync(Uri uri, string outputPath, CancellationToken token)
    {
        string tempPath = outputPath + ".tmp";
        using (var resp = await _downloadHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(fs, 81920, token).ConfigureAwait(false);
        }

        // replace
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
    }

    private async Task DownloadChunkedFileAsync(Uri uri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, CancellationToken token)
    {
        string tempPath = outputPath + ".tmp";
        // ensure empty temp
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            foreach (var chunk in chunkInfos)
            {
                token.ThrowIfCancellationRequested();

                long start = (long)chunk.Start;
                long end = (long)chunk.End;

                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                using HttpResponseMessage resp = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await content.CopyToAsync(fs, 81920, token).ConfigureAwait(false);
            }
        }

        // replace
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
    }

    private async Task<long> CalculateDownloadedBytesAsync(CancellationToken token)
    {
        // Downloaded size is calculated from files present in the installation directory.
        // For partially downloaded files we count the temporary ".tmp" file if present.
        // This provides a conservative estimate of already downloaded bytes.
        try
        {
            var index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);
            if (index?.Resource == null || index.Resource.Length == 0)
                return 0L;

            string? installPath = null;
            GameManager.GetGamePath(out installPath);
            if (string.IsNullOrEmpty(installPath))
                return 0L;

            long total = 0L;
            foreach (var entry in index.Resource)
            {
                token.ThrowIfCancellationRequested();

                if (entry == null || string.IsNullOrEmpty(entry.Dest))
                    continue;

                string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                string outputPath = Path.Combine(installPath, relativePath);
                string tempPath = outputPath + ".tmp";

                // If final file exists -> count its actual size
                if (File.Exists(outputPath))
                {
                    try
                    {
                        var fi = new FileInfo(outputPath);
                        total += fi.Length;
                        continue;
                    }
                    catch
                    {
                        // ignore and try temp fallback
                    }
                }

                // Otherwise if temp exists (partial download), count its size
                if (File.Exists(tempPath))
                {
                    try
                    {
                        var tfi = new FileInfo(tempPath);
                        total += tfi.Length;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // If neither exists, nothing added for this entry
            }

            return total;
        }
        catch (OperationCanceledException)
        {
            return 0L;
        }
        catch
        {
            // on any error return 0 to avoid crashing callers
            return 0L;
        }
    }
}
