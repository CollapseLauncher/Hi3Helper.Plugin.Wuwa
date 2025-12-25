using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable LoopCanBeConvertedToQuery

namespace Hi3Helper.Plugin.Wuwa.Management;

[GeneratedComClass]
internal partial class WuwaGameInstaller : GameInstallerBase
{
    private const long   Md5CheckSizeThreshold   = 50L * 1024L * 1024L; // 50 MB
    private const double ExCacheDurationInMinute = 10d;

    private DateTimeOffset _cacheExpiredUntil = DateTimeOffset.MinValue;
    private WuwaApiResponseResourceIndex? _currentIndex;

    private string? GameAssetBaseUrl => (GameManager as WuwaGameManager)?.GameResourceBaseUrl;
    private string? GameResourceBasisPath => (GameManager as WuwaGameManager)?.GameResourceBasisPath;
    private string? ApiResponseAssetUrl => (GameManager as WuwaGameManager)?.ApiResponseAssetUrl;

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
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::InitAsync] Index cached: {Count} entries", _currentIndex?.Resource.Length ?? 0);
        }
        catch (Exception ex)
        {
            // Ignore errors here; downstream code handles missing index gracefully.
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::InitAsync] Failed to warm index cache: {Err}", ex.Message);
            _currentIndex = null;
        }

        UpdateCacheExpiration();
        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::InitAsync] Init complete.");

        GameManager.GetApiGameVersion(out GameVersion apiVer);
        GameManager.GetCurrentGameVersion(out GameVersion curVer);
        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller] ApiGameVersion={Api} CurrentGameVersion={Current}", apiVer, curVer);
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
                total = unchecked(total + r.Size);
            }

            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetGameSizeAsyncInner] Computed total size: {Total}", total);
            return total > long.MaxValue ? long.MaxValue : (long)total;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::GetGameSizeAsyncInner] Error computing total size: {Err}", ex.Message);
            return 0L;
        }
    }

	protected override async Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
	{
        await StartInstallCoreAsync(GameInstallerKind.Install, progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
	}

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // reuse install routine for preload but indicate kind Preload
        return StartInstallCoreAsync(GameInstallerKind.Preload, progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // call shared routine with Update kind so we can detect and skip unchanged files
        return StartInstallCoreAsync(GameInstallerKind.Update, progressDelegate, progressStateDelegate, token);
    }

    // Delegate installation flow to the Install helper (defined in the separate partial file).
    private Task StartInstallCoreAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        var installer = new Install(this);
        return installer.RunAsync(kind, progressDelegate, progressStateDelegate, token);
    }

    protected override async Task UninstallAsyncInner(CancellationToken token)
    {
        bool isInstalled;
        GameManager.IsGameInstalled(out isInstalled);
        if (!isInstalled)
            return;

        string? installPath;
        GameManager.GetGamePath(out installPath);
        if (string.IsNullOrEmpty(installPath))
            return;

        await Task.Run(() => Directory.Delete(installPath, true), token).ConfigureAwait(false);
    }

    public override void Dispose()
	{
		_downloadHttpClient.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Helpers
	// NOTE: ComputeMd5Hex has been moved to WuwaUtils.
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

            GameManager.GetGamePath(out string? installPath);
            if (string.IsNullOrEmpty(installPath))
                return 0L;

            var tempDirPath = Path.Combine(installPath, "TempPath", "TempGameFiles");

            long total = 0L;
            foreach (var entry in index.Resource)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Dest))
                    continue;

                string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                string outputPath = Path.Combine(tempDirPath, relativePath);
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
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::CalculateDownloadedBytesAsync] Error reading file info {File}: {Err}", outputPath, ex.Message);
                        // ignore and try temp fallback
                    }
                }

				// If the temporary file doesn't exist, skip
                if (!File.Exists(tempPath)) continue;

                // Otherwise if temp exists (partial download), count its size
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

	// This method provides a cached (with expiration) or fresh download of the resource index
	// It uses the _currentIndex field and _cacheExpiredUntil to manage cache expiration
	private async Task<WuwaApiResponseResourceIndex?> GetCachedIndexAsync(bool forceRefresh, CancellationToken token)
	{
		// Return cached if valid and not forced
		if (!forceRefresh && _currentIndex != null && DateTimeOffset.UtcNow <= _cacheExpiredUntil)
		{
			SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::GetCachedIndexAsync] Returning cached index (entries={Count})", _currentIndex?.Resource.Length ?? 0);
			return _currentIndex;
		}

		if (GameAssetBaseUrl is null)
		{
			SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::GetCachedIndexAsync] GameAssetBaseUrl is null.");
			throw new InvalidOperationException("Game asset base URL is not initialized.");
		}

		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetCachedIndexAsync] Downloading index from: {Url}", GameAssetBaseUrl);

		try
		{
			// Use the robust JSON parsing helper (handles case-insensitive keys, strings/numbers, chunkInfos, etc.)
			var downloaded = await DownloadResourceIndexAsync(GameAssetBaseUrl, token).ConfigureAwait(false);
			if (downloaded == null)
			{
				SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::GetCachedIndexAsync] DownloadResourceIndexAsync returned null for URL: {Url}", GameAssetBaseUrl);
				// If we have a previous cached index and this wasn't forced, return it as a fallback
				if (!forceRefresh && _currentIndex != null)
					return _currentIndex;

				return null;
			}

			_currentIndex = downloaded;
			UpdateCacheExpiration();
			SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::GetCachedIndexAsync] Cached index updated: {Count} entries", _currentIndex?.Resource.Length ?? 0);
			return _currentIndex;
		}
		catch (Exception ex)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::GetCachedIndexAsync] Failed to fetch/parse index: {Err}", ex.Message);
			if (!forceRefresh && _currentIndex != null)
				return _currentIndex;
			return null;
		}
	}

	private void UpdateCacheExpiration()
	{
		_cacheExpiredUntil = DateTimeOffset.UtcNow.AddMinutes(ExCacheDurationInMinute);
	}

	private async Task<WuwaApiResponseResourceIndex?> DownloadResourceIndexAsync(string url, CancellationToken token)
	{
		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::DownloadResourceIndexAsync] Requesting index URL: {Url}", url);
		using HttpResponseMessage resp = await _downloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

		if (!resp.IsSuccessStatusCode)
		{
			string bodyPreview = string.Empty;
			try { bodyPreview = (await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim(); }
            catch
            {
                // ignored
            }

            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadResourceIndexAsync] GET {Url} returned {Status}. Body preview: {Preview}", url, resp.StatusCode, bodyPreview.Length > 400 ? bodyPreview[..400] + "..." : bodyPreview);
			return null;
		}

		await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

		try
		{
			using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
			JsonElement root = doc.RootElement;

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
						if ((sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetUInt64(out ulong uv)) ||
                            (sizeEl.ValueKind == JsonValueKind.String && ulong.TryParse(sizeEl.GetString(), out uv)))
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
							if ((startEl.ValueKind == JsonValueKind.Number && startEl.TryGetUInt64(out ulong sv)) ||
                                (startEl.ValueKind == JsonValueKind.String && ulong.TryParse(startEl.GetString(), out sv)))
								ci.Start = sv;
                        }

						if (TryGetPropertyCI(c, "end", out JsonElement endEl))
						{
							if ((endEl.ValueKind == JsonValueKind.Number && endEl.TryGetUInt64(out ulong ev)) ||
                                (endEl.ValueKind == JsonValueKind.String && ulong.TryParse(endEl.GetString(), out ev)))
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

            // Case-insensitive property lookup helper
            // ReSharper disable once InconsistentNaming
            static bool TryGetPropertyCI(JsonElement el, string propName, out JsonElement value)
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    value = default;
                    return false;
                }

                foreach (var p in el.EnumerateObject())
                {
                    if (!string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase)) continue;
                    value = p.Value;
                    return true;
                }

                value = default;
                return false;
            }
        }
		catch (JsonException ex)
		{
			// Malformed JSON or parse error
			SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadResourceIndexAsync] JSON parse error: {Err}", ex.Message);
			return null;
		}
	}
	#endregion
}
