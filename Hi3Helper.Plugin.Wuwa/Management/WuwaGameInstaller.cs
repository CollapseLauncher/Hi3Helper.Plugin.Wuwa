using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;

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

    // Override InitAsync to initialize the installer (and avoid calling the base InitializableTask.InitAsync).
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::InitAsync] Entering InitAsync (warm index cache). Force refresh.");

        // Delegate core initialization to the manager if available, then warm the resource index cache.
        if (GameManager is not WuwaGameManager asWuwaManager)
            throw new InvalidOperationException("GameManager is not a WuwaGameManager and cannot initialize Wuwa installer.");

        // Call manager's init logic (internal InitAsyncInner) to populate config and GameResourceBaseUrl.
        int mgrResult = await asWuwaManager.InitAsyncInner(true, token).ConfigureAwait(false);

        // Attempt to download and cache the resource index (don't fail hard if index is missing; callers handle null).
        try
        {
            _currentIndex = await GetCachedIndexAsync(true, token).ConfigureAwait(false);
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::InitAsync] Index cached: {Count} entries", _currentIndex?.Resource?.Length ?? 0);
        }
        catch (Exception ex)
        {
            // Ignore errors here; downstream code handles missing index gracefully.
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::InitAsync] Failed to warm index cache: {Err}", ex.Message);
            _currentIndex = null;
        }

        UpdateCacheExpiration();
        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::InitAsync] Init complete.");
        return mgrResult;
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
        {
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetGameSizeAsyncInner] Index empty or null");
            return 0L;
        }

        try
        {
            // Sum sizes; clamp to long.MaxValue to avoid overflow
            ulong total = 0;
            foreach (var r in index.Resource)
            {
                if (r?.Size != null)
                    total = unchecked(total + r.Size);
            }

            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetGameSizeAsyncInner] Computed total size: {Total}", total);
            return total > (ulong)long.MaxValue ? long.MaxValue : (long)total;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::GetGameSizeAsyncInner] Error computing total size: {Err}", ex.Message);
            return 0L;
        }
    }

    /// <summary>
    /// Start install: downloads indexFile.json, iterates entries and downloads each resource.
    /// Supports chunked resources via HTTP Range requests when chunkInfos are present.
    /// Writes files under the current install path (GameManager.SetGamePath expected to be set).
    /// Reports progress through provided delegates.
    /// </summary>
    protected override async Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Starting installation routine.");

        if (GameAssetBaseUrl is null)
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] GameAssetBaseUrl is null, aborting.");
            throw new InvalidOperationException("Game asset base URL is not initialized.");
        }

        // Ensure initialization (loads API/game config)
        await InitAsync(token).ConfigureAwait(false);

        // Download index JSON (use cached)
        WuwaApiResponseResourceIndex? index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);
        if (index?.Resource == null || index.Resource.Length == 0)
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Resource index is empty, aborting install.");
            throw new InvalidOperationException("Resource index is empty.");
        }

        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Resource index loaded. Entries: {Count}", index.Resource.Length);

        string? installPath = null;
        GameManager.GetGamePath(out installPath);

        if (string.IsNullOrEmpty(installPath))
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Install path isn't set, aborting.");
            throw new InvalidOperationException("Game install path isn't set.");
        }

        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Install path: {Path}", installPath);

        // Base URI for resources: GameAssetBaseUrl typically ends with indexFile.json
        Uri baseUri = new(GameAssetBaseUrl, UriKind.Absolute);
        string baseDirectory = baseUri.GetLeftPart(UriPartial.Path);
        // remove the index file part to get the directory
        int lastSlash = baseDirectory.LastIndexOf('/');
        if (lastSlash >= 0)
            baseDirectory = baseDirectory.Substring(0, lastSlash + 1);

        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Base directory for resources: {BaseDir}", baseDirectory);

        long totalBytesToDownload = 0;
        foreach (var r in index.Resource)
            totalBytesToDownload += (long)(r?.Size ?? 0);

        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Total bytes to download (sum of index sizes): {TotalBytes}", totalBytesToDownload);

        long downloadedBytes = 0;

        // Prepare an InstallProgress instance if available and keep it updated
        InstallProgress installProgress = default;
        try
        {
            // Initialize fields commonly used by UI
            installProgress.DownloadedCount = 0;
            installProgress.TotalCountToDownload = index.Resource.Length;
            installProgress.DownloadedBytes = 0;
            installProgress.TotalBytesToDownload = totalBytesToDownload;
        }
        catch
        {
            // If InstallProgress is not available or shape differs, ignore initialization and still try to call delegates where possible.
        }

        // helper callback used by download helpers to report byte increments
        void OnBytesWritten(long delta)
        {
            // delta can be negative in some validation scenarios (not used here) but keep handling generic
            downloadedBytes += delta;
            try
            {
                installProgress.DownloadedBytes = downloadedBytes;
                installProgress.TotalBytesToDownload = totalBytesToDownload;
                progressDelegate?.Invoke(in installProgress);
            }
            catch
            {
                // If delegate signatures differ, swallow errors to avoid crashing the installer.
                progressDelegate?.Invoke(in installProgress);
            }
        }

        foreach (var entry in index.Resource)
        {
            token.ThrowIfCancellationRequested();

            if (entry == null || string.IsNullOrEmpty(entry.Dest))
            {
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Skipping null or empty entry.");
                continue;
            }

            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Processing entry: Dest={Dest}, Size={Size}, Md5={Md5}, Chunks={Chunks}",
                entry.Dest, entry.Size, entry.Md5, entry.ChunkInfos?.Length ?? 0);

            string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
            string outputPath = Path.Combine(installPath, relativePath);
            string? parentDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Creating directory: {Dir}", parentDir);
                Directory.CreateDirectory(parentDir);
            }

            // Signal starting this entry
            try
            {
                installProgress.DownloadedCount++;
                progressStateDelegate?.Invoke(InstallProgressState.Download);
                progressDelegate?.Invoke(in installProgress);
            }
            catch
            {
                // ignore delegate invocation errors
            }

            // If file exists and md5 matches, skip download
            if (File.Exists(outputPath))
            {
                try
                {
                    var fi = new FileInfo(outputPath);
                    SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] File exists: {File} (len={Len})", outputPath, fi.Length);

                    if (!string.IsNullOrEmpty(entry.Md5))
                    {
                        using var fs = File.OpenRead(outputPath);
                        string currentMd5 = ComputeMD5Hex(fs);
                        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Existing file md5={Md5Existing}, expected={Md5Expected}", currentMd5, entry.Md5);

                        if (string.Equals(currentMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadedBytes += (long)entry.Size;
                            try
                            {
                                installProgress.DownloadedBytes = downloadedBytes;
                                progressDelegate?.Invoke(in installProgress);
                            }
                            catch { }

                            SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Skipping entry (already valid): {Dest}", entry.Dest);
                            continue;
                        }
                        else
                        {
                            SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Existing file md5 mismatch; re-downloading: {Dest}", entry.Dest);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Error while checking existing file: {Err}", ex.Message);
                    // fallback to re-download
                }
            }

            // Download either as whole file or by chunks
            if (entry.ChunkInfos == null || entry.ChunkInfos.Length == 0)
            {
                // whole file
                Uri fileUri = new Uri(new Uri(baseDirectory), entry.Dest);
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Downloading whole file. URI: {Uri}", fileUri);
                await DownloadWholeFileAsync(fileUri, outputPath, token, OnBytesWritten).ConfigureAwait(false);
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Downloaded file: {Path}", outputPath);
            }
            else
            {
                // chunked: stream into a temp file and append chunks
                Uri fileUri = new Uri(new Uri(baseDirectory), entry.Dest);
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Downloading chunked file. URI: {Uri}, Chunks: {Chunks}", fileUri, entry.ChunkInfos.Length);
                await DownloadChunkedFileAsync(fileUri, outputPath, entry.ChunkInfos, token, OnBytesWritten).ConfigureAwait(false);
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Downloaded chunked file: {Path}", outputPath);
            }

            // Verify MD5 if provided
            if (!string.IsNullOrEmpty(entry.Md5))
            {
                try
                {
                    using var fsVerify = File.OpenRead(outputPath);
                    string md5 = ComputeMD5Hex(fsVerify);
                    if (!string.Equals(md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] MD5 mismatch for {Dest}. Expected {Expected}, got {Got}", entry.Dest, entry.Md5, md5);
                        throw new InvalidOperationException($"MD5 mismatch for {entry.Dest}: expected {entry.Md5}, got {md5}");
                    }

                    SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] MD5 verified for {Dest}", entry.Dest);
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] MD5 verification failed for {Dest}: {Err}", entry.Dest, ex.Message);
                    throw;
                }
            }

            // mark file as downloaded in counts (already incremented when starting)
            try
            {
                // make sure downloaded bytes accounted (entry.Size should be included via OnBytesWritten already)
                installProgress.DownloadedBytes = downloadedBytes;
                progressDelegate?.Invoke(in installProgress);
            }
            catch { }

            // Note: additional state updates (e.g. progressStateDelegate to Completed) can be invoked here if desired.
        }

        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Install loop finished. Downloaded bytes sum: {Downloaded}", downloadedBytes);

        // Installation finished: set current version, save config and write minimal app-game-config.json
        try
        {
            // Update current game version and save plugin config so launcher recognizes installed version
            GameManager.GetApiGameVersion(out GameVersion latestVersion);
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] API latest version: {Version}", latestVersion);
            if (latestVersion != GameVersion.Empty)
            {
                GameManager.SetCurrentGameVersion(latestVersion);
                GameManager.SaveConfig();
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Saved current game version to config.");
            }

            // Write a minimal app-game-config.json so other code that reads this file can find a version/index reference.
            try
            {
                string configPath = Path.Combine(installPath, "app-game-config.json");
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Writing app-game-config.json to {Path}", configPath);
                using var ms = new MemoryStream();
                var writerOptions = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                using (var writer = new Utf8JsonWriter(ms, writerOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteString("version", latestVersion == GameVersion.Empty ? string.Empty : latestVersion.ToString());
                    // attempt to include indexFile filename if possible
                    try
                    {
                        var idxName = new Uri(GameAssetBaseUrl ?? string.Empty, UriKind.Absolute).AbsolutePath;
                        writer.WriteString("indexFile", Path.GetFileName(idxName));
                    }
                    catch
                    {
                        // ignore
                    }
                    writer.WriteEndObject();
                    writer.Flush();
                }

                byte[] buffer = ms.ToArray();
                await File.WriteAllBytesAsync(configPath, buffer, token).ConfigureAwait(false);
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Wrote app-game-config.json (size={Size})", buffer.Length);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Failed to write app-game-config.json: {Err}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Post-install actions failed: {Err}", ex.Message);
        }

        // Ensure the UI/host knows installation completed and refresh config if possible.
        try
        {
            // Refresh manager config/load to ensure any consumers reading config see the up-to-date state.
            GameManager.LoadConfig();

            // Notify state change to "installed" and send a final progress update.
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            installProgress.DownloadedBytes = downloadedBytes;
            progressDelegate?.Invoke(in installProgress);
        }
        catch (Exception ex)
        {
            // If the enum value or delegate doesn't exist in certain builds, swallow to avoid crashing the installer.
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Finalizing install state failed or not available: {Err}", ex.Message);
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
        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::DownloadResourceIndexAsync] Requesting index URL: {Url}", indexUrl);
        using HttpResponseMessage resp = await _downloadHttpClient.GetAsync(indexUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        // Reflection-based System.Text.Json is disabled in the host; parse with JsonDocument to avoid needing source-gen.
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            JsonElement root = doc.RootElement;

            // Case-insensitive property lookup helper
            static bool TryGetPropertyCI(JsonElement el, string propName, out JsonElement value)
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    value = default;
                    return false;
                }

                foreach (var p in el.EnumerateObject())
                {
                    if (string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = p.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            if (!TryGetPropertyCI(root, "resource", out JsonElement resourceElem) || resourceElem.ValueKind != JsonValueKind.Array)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::DownloadResourceIndexAsync] Index JSON contains no 'resource' array.");
                return null;
            }

            var list = new System.Collections.Generic.List<WuwaApiResponseResourceEntry>(resourceElem.GetArrayLength());

            foreach (var item in resourceElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = new WuwaApiResponseResourceEntry();

                if (TryGetPropertyCI(item, "dest", out JsonElement destEl) && destEl.ValueKind == JsonValueKind.String)
                    entry.Dest = destEl.GetString();

                if (TryGetPropertyCI(item, "md5", out JsonElement md5El) && md5El.ValueKind == JsonValueKind.String)
                    entry.Md5 = md5El.GetString();

                // size may be number or string
                if (TryGetPropertyCI(item, "size", out JsonElement sizeEl))
                {
                    try
                    {
                        if (sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetUInt64(out ulong uv))
                            entry.Size = uv;
                        else if (sizeEl.ValueKind == JsonValueKind.String && ulong.TryParse(sizeEl.GetString(), out uv))
                            entry.Size = uv;
                    }
                    catch
                    {
                        entry.Size = 0;
                    }
                }

                // chunkInfos (optional)
                if (TryGetPropertyCI(item, "chunkInfos", out JsonElement chunksEl) && chunksEl.ValueKind == JsonValueKind.Array)
                {
                    var chunkList = new System.Collections.Generic.List<WuwaApiResponseResourceChunkInfo>(chunksEl.GetArrayLength());
                    foreach (var c in chunksEl.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object)
                            continue;

                        var ci = new WuwaApiResponseResourceChunkInfo();

                        if (TryGetPropertyCI(c, "start", out JsonElement startEl))
                        {
                            if (startEl.ValueKind == JsonValueKind.Number && startEl.TryGetUInt64(out ulong sv))
                                ci.Start = sv;
                            else if (startEl.ValueKind == JsonValueKind.String && ulong.TryParse(startEl.GetString(), out sv))
                                ci.Start = sv;
                        }

                        if (TryGetPropertyCI(c, "end", out JsonElement endEl))
                        {
                            if (endEl.ValueKind == JsonValueKind.Number && endEl.TryGetUInt64(out ulong ev))
                                ci.End = ev;
                            else if (endEl.ValueKind == JsonValueKind.String && ulong.TryParse(endEl.GetString(), out ev))
                                ci.End = ev;
                        }

                        if (TryGetPropertyCI(c, "md5", out JsonElement cMd5El) && cMd5El.ValueKind == JsonValueKind.String)
                            ci.Md5 = cMd5El.GetString();

                        chunkList.Add(ci);
                    }

                    entry.ChunkInfos = chunkList.ToArray();
                }

                list.Add(entry);
            }

            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::DownloadResourceIndexAsync] Parsed index entries: {Count}", list.Count);
            return new WuwaApiResponseResourceIndex { Resource = list.ToArray() };
        }
        catch (JsonException ex)
        {
            // Malformed JSON or parse error; return null and let callers handle defensively.
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadResourceIndexAsync] JSON parse error: {Err}", ex.Message);
            return null;
        }
    }

    private async Task<WuwaApiResponseResourceIndex?> GetCachedIndexAsync(bool force, CancellationToken token)
    {
        if (!force && _currentIndex != null && DateTimeOffset.UtcNow <= _cacheExpiredUntil)
        {
            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::GetCachedIndexAsync] Returning cached index (entries={Count})", _currentIndex?.Resource?.Length ?? 0);
            return _currentIndex;
        }

        if (GameAssetBaseUrl is null)
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::GetCachedIndexAsync] GameAssetBaseUrl is null.");
            throw new InvalidOperationException("Game asset base URL is not initialized.");
        }

        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetCachedIndexAsync] Downloading index from: {Url}", GameAssetBaseUrl);
        _currentIndex = await DownloadResourceIndexAsync(GameAssetBaseUrl, token).ConfigureAwait(false);
        UpdateCacheExpiration();
        return _currentIndex;
    }

    private void UpdateCacheExpiration() => _cacheExpiredUntil = DateTimeOffset.UtcNow.AddMinutes(ExCacheDurationInMinute);

    private async Task DownloadWholeFileAsync(Uri uri, string outputPath, CancellationToken token, Action<long>? progressCallback)
    {
        string tempPath = outputPath + ".tmp";
        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Downloading {Uri} -> {Temp}", uri, tempPath);
        using (var resp = await _downloadHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            // ensure temp file is created (overwrite if exists)
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            using FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                    progressCallback?.Invoke(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // replace
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Moved {Temp} -> {Out}", tempPath, outputPath);
    }

    private async Task DownloadChunkedFileAsync(Uri uri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, CancellationToken token, Action<long>? progressCallback)
    {
        string tempPath = outputPath + ".tmp";
        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Downloading chunks for {Uri} -> {Temp}", uri, tempPath);
        // ensure empty temp
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
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

                    int read;
                    while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        progressCallback?.Invoke(read);
                    }

                    SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Wrote chunk {Start}-{End} to temp", start, end);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // replace
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Moved {Temp} -> {Out}", tempPath, outputPath);
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
            {
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Index empty/null.");
                return 0L;
            }

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
                        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Counted existing file {File} len={Len}", outputPath, fi.Length);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Error reading file info {File}: {Err}", outputPath, ex.Message);
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
                        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Counted temp file {Temp} len={Len}", tempPath, tfi.Length);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // If neither exists, nothing added for this entry
            }

            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Total counted downloaded bytes: {Total}", total);
            return total;
        }
        catch (OperationCanceledException)
        {
            return 0L;
        }
        catch (Exception ex)
        {
            // on any error return 0 to avoid crashing callers
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Error: {Err}", ex.Message);
            return 0L;
        }
    }
}
