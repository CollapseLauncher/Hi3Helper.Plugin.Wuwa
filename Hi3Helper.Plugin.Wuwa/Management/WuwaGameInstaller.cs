using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
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

	// Changes: improved TotalCountToDownload calculation and added diagnostic logging before emitting initial progress.
	protected override async Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
	{
		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Starting installation routine.");

		if (GameAssetBaseUrl is null)
		{
			SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] GameAssetBaseUrl is null, aborting.");
			throw new InvalidOperationException("Game asset base URL is not initialized.");
		}

        if (GameResourceBasisPath is null)
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] GameResourceBasisPath is null, aborting.");
            throw new InvalidOperationException("Game resource basis path is not initialized.");
        }

        if (ApiResponseAssetUrl is null)
        {
            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] ApiResponseAssetUrl is null, aborting.");
            throw new InvalidOperationException("Api Response Asset Url is not initialized.");
        }

        // Ensure initialization (loads API/game config)
        await InitAsync(token).ConfigureAwait(false);

		// Download index JSON (use cached, but force refresh on first failure)
		WuwaApiResponseResourceIndex? index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);

		// If cached index is empty/null, try a forced refresh and provide detailed logs
		if (index?.Resource == null || index.Resource.Length == 0)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Cached index empty (entries={Count}). Forcing refresh from: {Url}", index?.Resource.Length ?? -1, GameAssetBaseUrl);
			try
			{
				index = await GetCachedIndexAsync(true, token).ConfigureAwait(false);
				SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Forced index refresh result: entries={Count}", index?.Resource.Length ?? -1);
			}
			catch (Exception ex)
			{
				SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Forced index refresh failed: {Err}", ex.Message);
				throw new InvalidOperationException($"Resource index is empty and forced refresh failed. See logs for details. URL={GameAssetBaseUrl}", ex);
			}

			if (index?.Resource == null || index.Resource.Length == 0)
			{
				SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Resource index is empty even after forced refresh. URL={Url}", GameAssetBaseUrl);
				throw new InvalidOperationException("Resource index is empty.");
			}
		}

		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Resource index loaded. Entries: {Count}", index.Resource.Length);
        GameManager.GetGamePath(out string? installPath);

		if (string.IsNullOrEmpty(installPath))
		{
			SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Install path isn't set, aborting.");
			throw new InvalidOperationException("Game install path isn't set.");
		}

		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Install path: {Path}", installPath);

		// Base URI for resources: GameAssetBaseUrl typically ends with indexFile.json
		Uri baseUri = new(GameAssetBaseUrl, UriKind.Absolute);

		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Base directory for resources: {BaseDir}", GameResourceBasisPath);

		long totalBytesToDownload = 0;
		foreach (var r in index.Resource)
			totalBytesToDownload += (long)r.Size;

		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Total bytes to download (sum of index sizes): {TotalBytes}", totalBytesToDownload);

		// Calculate initial downloaded bytes from disk to ensure UI sees meaningful values immediately.
		long downloadedBytes;
		try
		{
			downloadedBytes = await CalculateDownloadedBytesAsync(token).ConfigureAwait(false);
			SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Initial downloaded bytes from disk: {DownloadedBytes}", downloadedBytes);
		}
		catch (Exception ex)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Failed to compute initial downloaded bytes: {Err}", ex.Message);
			downloadedBytes = 0;
		}

		// Avoid reporting >100%: clamp to totalBytesToDownload (if known)
		if (totalBytesToDownload > 0 && downloadedBytes > totalBytesToDownload)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Downloaded bytes on disk ({Disk}) exceed index total ({Index}). Clamping reported downloaded bytes.", downloadedBytes, totalBytesToDownload);
			downloadedBytes = totalBytesToDownload;
		}

		// Compute how many entries are actually downloadable (non-null + have a Dest).
		// Use a simple, permissive predicate: count any entry with a non-empty Dest.
		int totalCountToDownload = 0;
		try
		{
			foreach (var e in index.Resource)
			{
                string? dest = e.Dest;
				if (string.IsNullOrWhiteSpace(dest))
					continue;

				// Count it as a downloadable entry; validation/download flow will handle directories/invalid dests.
				totalCountToDownload++;
			}
		}
		catch (Exception ex)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Error while computing TotalCountToDownload: {Err}", ex.Message);
		}

		// Compute how many files are already present and valid (to seed the file counter).
		// Track the set of paths we considered "already downloaded" so we don't double-count later.
		var seededPaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int alreadyDownloadedCount = 0;
		try
		{

			foreach (var e in index.Resource)
			{
				token.ThrowIfCancellationRequested();
				if (string.IsNullOrEmpty(e.Dest))
					continue;

				string relativePath = e.Dest.Replace('/', Path.DirectorySeparatorChar);
				string outputPath = Path.Combine(installPath, relativePath);

				if (!File.Exists(outputPath))
					continue;

				try
				{
					var fi = new FileInfo(outputPath);

					// Prefer size comparison (very fast) if size info present in index.
					if (e.Size > 0)
                    {
                        if (fi.Length == (long)e.Size)
						{
							alreadyDownloadedCount++;
							seededPaths.Add(outputPath);
                        }
						else
						{
							SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Size mismatch for seeding: {Path} (disk={Disk}, index={Index})", outputPath, fi.Length, e.Size);
                        }

                        continue;
                    }

					// If no size but MD5 is provided and file is reasonably small, compute MD5.
					if (!string.IsNullOrEmpty(e.Md5) && fi.Length <= Md5CheckSizeThreshold)
					{
                        await using var fs      = File.OpenRead(outputPath);
                        string          fileMd5 = await WuwaUtils.ComputeMd5HexAsync(fs, token);
                        if (string.Equals(fileMd5, e.Md5, StringComparison.OrdinalIgnoreCase))
						{
							alreadyDownloadedCount++;
							seededPaths.Add(outputPath);
                        }
						else
						{
							SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] MD5 mismatch during seeding: {Path}", outputPath);
                        }
					}

					// No reliable quick check possible (either no size, MD5 too expensive, or MD5 missing).
					// Treat as not downloaded so installer will (re)validate/download it.
				}
				catch (Exception ex)
				{
					SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Error while checking existing file during seeding: {Err}", ex.Message);
					// ignore individual file errors
				}
			}
		}
		catch (OperationCanceledException)
		{
			// if cancelled while seeding counts, proceed with what we have
		}

		// diagnostic log to help troubleshoot 0/0 case
		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Seeding counts: TotalCountToDownload={TotalCount}, AlreadyDownloadedCount={Already}, IndexEntries={IndexEntries}",
			totalCountToDownload, alreadyDownloadedCount, index.Resource.Length);

		// Prepare an InstallProgress instance and set deterministic initial values
		InstallProgress installProgress = default;
		installProgress.DownloadedCount = alreadyDownloadedCount;
		installProgress.TotalCountToDownload = totalCountToDownload;
		installProgress.DownloadedBytes = downloadedBytes;
		installProgress.TotalBytesToDownload = totalBytesToDownload;

		// Send an initial progress/state update so UI sees non-zero totals immediately.
		try
		{
			progressStateDelegate?.Invoke(InstallProgressState.Preparing);
		}
		catch
		{
			// Swallow to avoid crashes; UI may be incompatible on some hosts.
		}

		try
		{
			progressDelegate?.Invoke(in installProgress);
		}
		catch
		{
			// Swallow to avoid crashes; at least internal state is initialized now.
		}

        foreach (var entry in index.Resource)
		{
			token.ThrowIfCancellationRequested();

			if (string.IsNullOrEmpty(entry.Dest))
			{
				SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Skipping null or empty entry.");
				continue;
			}
#if DEBUG
			SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Processing entry: Dest={Dest}, Size={Size}, Md5={Md5}, Chunks={Chunks}",
				entry.Dest, entry.Size, entry.Md5, entry.ChunkInfos?.Length ?? 0);
#endif

			string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
			string outputPath = Path.Combine(installPath, relativePath);
			string? parentDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
			{
				SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Creating directory: {Dir}", parentDir);
				Directory.CreateDirectory(parentDir);
			}

			// Check existing file before starting download
			bool skipBecauseValid = false;
			if (File.Exists(outputPath))
			{
				try
				{
					var fi = new FileInfo(outputPath);
					// Prefer quick size check if index has it
					if (entry.Size > 0)
					{
						if (fi.Length == (long)entry.Size)
							skipBecauseValid = true;
						else
							SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Existing file size mismatch; re-downloading: {Dest}", entry.Dest);
					}
					else if (!string.IsNullOrEmpty(entry.Md5))
					{
						// Only compute MD5 for reasonably small files to avoid long blocks
						if (fi.Length <= Md5CheckSizeThreshold)
						{
                            await using var fs         = File.OpenRead(outputPath);
                            string          currentMd5 = await WuwaUtils.ComputeMd5HexAsync(fs, token);
                            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Existing file md5={Md5Existing}, expected={Md5Expected}", currentMd5, entry.Md5);

							if (string.Equals(currentMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
								skipBecauseValid = true;
							else
								SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Existing file md5 mismatch; re-download: {Dest}", entry.Dest);
						}
						else
						{
							SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Skipping MD5 validation for large existing file during runtime: {File}", outputPath);
							// Treat as not valid -> re-download to be safe (avoids blocking)
						}
					}
					// else: no md5 and unknown size -> treat as not valid (re-download)
				}
				catch (Exception ex)
				{
					SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Error while checking existing file: {Err}", ex.Message);
					// fallback to re-download
				}
			}

			if (skipBecauseValid)
			{
				// If this path wasn't counted during seeding, count it now so the file counter reflects reality.
				if (seededPaths.Add(outputPath))
				{
                    installProgress.DownloadedCount++;
				}

				// update bytes/progress
				try
				{
					installProgress.DownloadedBytes = downloadedBytes > totalBytesToDownload && totalBytesToDownload > 0 ? totalBytesToDownload : downloadedBytes;
					installProgress.TotalBytesToDownload = totalBytesToDownload;
					progressDelegate?.Invoke(in installProgress);
				}
                catch
                {
                    // ignored
                }

				continue;
			}

			// Signal state for currently downloading this entry
			try
			{
				progressStateDelegate?.Invoke(InstallProgressState.Download);
				progressDelegate?.Invoke(in installProgress);
			}
			catch
			{
				// ignore delegate invocation errors
			}

			// Download either as whole file or by chunks
			Uri fileUri = new Uri(new Uri(new Uri(ApiResponseAssetUrl), GameResourceBasisPath), entry.Dest);
			if (entry.ChunkInfos == null || entry.ChunkInfos.Length == 0)
			{
				// whole file with fallback attempts
				SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Downloading whole file. URI: {Uri}", fileUri);
				try
				{
					await TryDownloadWholeFileWithFallbacksAsync(fileUri, outputPath, entry.Dest, token, OnBytesWritten).ConfigureAwait(false);
					SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Downloaded file: {Path}", outputPath);
				}
				catch (Exception ex)
				{
					SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Failed to download file {Dest}: {Err}", entry.Dest, ex.Message);
					throw;
				}
			}
			else
			{
				// chunked: attempt chunked download; on 404 or failure, try encoded-path fallback once
				SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Downloading chunked file. URI: {Uri}, Chunks: {Chunks}", fileUri, entry.ChunkInfos.Length);
				try
				{
					await TryDownloadChunkedFileWithFallbacksAsync(fileUri, outputPath, entry.ChunkInfos, entry.Dest, token, OnBytesWritten).ConfigureAwait(false);
					SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Downloaded chunked file: {Path}", outputPath);
				}
				catch (Exception ex)
				{
					SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Failed to download chunked file {Dest}: {Err}", entry.Dest, ex.Message);
					throw;
				}
			}

			// Verify MD5 if provided
			if (!string.IsNullOrEmpty(entry.Md5))
			{
				try
				{
                    await using var fsVerify = File.OpenRead(outputPath);
                    string          md5      = await WuwaUtils.ComputeMd5HexAsync(fsVerify, token);
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

			// Completed this entry: increment completed-file counter and update progress
			try
			{
				// mark path as seeded so we won't double count if another check occurs
				seededPaths.Add(outputPath);
				installProgress.DownloadedCount++;
				installProgress.DownloadedBytes = downloadedBytes > totalBytesToDownload && totalBytesToDownload > 0 ? totalBytesToDownload : downloadedBytes;
				progressDelegate?.Invoke(in installProgress);
			}
            catch
            {
                // ignored
            }

            // Note: additional state updates (e.g. per-entry Completed) can be invoked here if desired.
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
                await using (var writer = new Utf8JsonWriter(ms, writerOptions))
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
					await writer.FlushAsync(token);
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
			installProgress.DownloadedBytes = downloadedBytes > totalBytesToDownload && totalBytesToDownload > 0 ? totalBytesToDownload : downloadedBytes;
			progressDelegate?.Invoke(in installProgress);
		}
		catch (Exception ex)
		{
			// If the enum value or delegate doesn't exist in certain builds, swallow to avoid crashing the installer.
			SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Finalizing install state failed or not available: {Err}", ex.Message);
		}

        return;

        // helper callback used by download helpers to report byte increments
        void OnBytesWritten(long delta)
        {
            // delta can be negative in some validation scenarios (not used here) but keep handling generic
            if (delta == 0) return;
            downloadedBytes += delta;

            // Keep reported downloaded bytes within the index total to avoid showing >100%
            long reportedBytes = downloadedBytes;
            if (totalBytesToDownload > 0 && reportedBytes > totalBytesToDownload)
                reportedBytes = totalBytesToDownload;

            try
            {
                installProgress.DownloadedBytes      = reportedBytes;
                installProgress.TotalBytesToDownload = totalBytesToDownload;
                progressDelegate?.Invoke(in installProgress);
            }
            catch
            {
                // Swallow delegate errors to avoid crashing the installer.
            }
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

    // ---------- Helpers ----------
    // Note for @Cry0. ComputeMd5Hex has been moved to WuwaUtils.

    // Robust Download helpers with fallbacks and diagnostic logs
    private async Task TryDownloadWholeFileWithFallbacksAsync(Uri originalUri, string outputPath, string rawDest, CancellationToken token, Action<long>? progressCallback)
    {
        // Try original first
        try
        {
            await DownloadWholeFileAsync(originalUri, outputPath, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Primary download failed: {Uri}. Reason: {Msg}", originalUri, hre.Message);
            // fall through to fallback attempts
        }

        // Build an encoded path (encode each segment, preserve slashes)
        string encodedPath = EncodePathSegments(rawDest);

        // Fallback 1: encoded concatenation using the Path portion of the original URI
        try
        {
            var basePath = originalUri.GetLeftPart(UriPartial.Path);
            string encodedConcatUrl = basePath.TrimEnd('/') + "/" + encodedPath;
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Trying encoded concatenation fallback URI: {Uri}", encodedConcatUrl);
            Uri fallbackUri = new Uri(encodedConcatUrl, UriKind.Absolute);
            await DownloadWholeFileAsync(fallbackUri, outputPath, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Encoded concatenation fallback failed: {Msg}", hre.Message);
        }

        // Fallback 2: try using a simple concatenation (encoded)
        try
        {
            var baseAuthority = originalUri.GetLeftPart(UriPartial.Authority);
            var baseDir = originalUri.AbsolutePath;
            int lastSlash = baseDir.LastIndexOf('/');
            if (lastSlash >= 0)
                baseDir = baseDir[..(lastSlash + 1)];
            string tryUrl = baseAuthority + baseDir + encodedPath;
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Trying authority+dir fallback URI: {Uri}", tryUrl);
            Uri fallbackUri2 = new Uri(tryUrl, UriKind.Absolute);
            await DownloadWholeFileAsync(fallbackUri2, outputPath, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Authority+dir fallback failed: {Msg}", hre.Message);
        }

        // No more fallbacks
        throw new HttpRequestException($"All download attempts failed for: {rawDest}");
    }

    private async Task TryDownloadChunkedFileWithFallbacksAsync(Uri originalUri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, string rawDest, CancellationToken token, Action<long>? progressCallback)
    {
        // Try original first
        try
        {
            await DownloadChunkedFileAsync(originalUri, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Primary chunked download failed: {Uri}. Reason: {Msg}", originalUri, hre.Message);
            // fall through to fallback attempts
        }

        // Build encoded path (encode each segment)
        string encodedPath = EncodePathSegments(rawDest);

        // Fallback 1: encoded concatenation using the Path portion of the original URI
        try
        {
            var basePath = originalUri.GetLeftPart(UriPartial.Path);
            string encodedConcatUrl = basePath.TrimEnd('/') + "/" + encodedPath;
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Trying encoded concatenation fallback URI: {Uri}", encodedConcatUrl);
            Uri fallbackUri = new Uri(encodedConcatUrl, UriKind.Absolute);
            await DownloadChunkedFileAsync(fallbackUri, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Encoded concatenation fallback failed: {Msg}", hre.Message);
        }

        // Fallback 2: authority+dir + encoded path
        try
        {
            var baseAuthority = originalUri.GetLeftPart(UriPartial.Authority);
            var baseDir = originalUri.AbsolutePath;
            int lastSlash = baseDir.LastIndexOf('/');
            if (lastSlash >= 0)
                baseDir = baseDir[..(lastSlash + 1)];
            string tryUrl = baseAuthority + baseDir + encodedPath;
            SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Trying authority+dir fallback URI: {Uri}", tryUrl);
            Uri fallbackUri2 = new Uri(tryUrl, UriKind.Absolute);
            await DownloadChunkedFileAsync(fallbackUri2, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException hre)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Authority+dir fallback failed: {Msg}", hre.Message);
        }

        throw new HttpRequestException($"All chunked download attempts failed for: {rawDest}");
    }

    private static string EncodePathSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        string[] parts = path.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", parts.Select(Uri.EscapeDataString));
    }

    private async Task DownloadWholeFileAsync(Uri uri, string outputPath, CancellationToken token, Action<long>? progressCallback)
    {
        string tempPath = outputPath + ".tmp";
        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Downloading {Uri} -> {Temp}", uri, tempPath);
        using (var resp = await _downloadHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                catch
                {
                    // ignored
                }

                SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadWholeFileAsync] Failed GET {Uri}: {Status}. Body preview: {BodyPreview}", uri, resp.StatusCode, body.Length > 200 ? body[..200] + "..." : body);
                throw new HttpRequestException($"Failed to GET {uri} : {(int)resp.StatusCode} {resp.StatusCode}", null, resp.StatusCode);
            }

            await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            // ensure temp file is created (overwrite if exists)
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            await using FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
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

        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
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
                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = string.Empty;
                        try { body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                        catch
                        {
                            // ignored
                        }

                        SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadChunkedFileAsync] Failed GET {Uri} (range {Start}-{End}): {Status}. Body preview: {BodyPreview}", uri, start, end, resp.StatusCode, body.Length > 200 ? body[..200] + "..." : body);
                        throw new HttpRequestException($"Failed to GET {uri} range {start}-{end} : {(int)resp.StatusCode} {resp.StatusCode}", null, resp.StatusCode);
                    }

                    await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

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

            GameManager.GetGamePath(out string? installPath);
            if (string.IsNullOrEmpty(installPath))
                return 0L;

            long total = 0L;
            foreach (var entry in index.Resource)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Dest))
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

	// Add this method to the WuwaGameInstaller class to fix CS0103.
	// This method provides a cached (with expiration) or fresh download of the resource index.
	// It uses the _currentIndex field and _cacheExpiredUntil to manage cache expiration.

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
	// Add this method to fix CS0103: The name 'DownloadResourceIndexAsync' does not exist in the current context.
	// This method downloads and parses the resource index JSON from the given URL.

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
			// Malformed JSON or parse error; return null and let callers handle defensively.
			SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadResourceIndexAsync] JSON parse error: {Err}", ex.Message);
			return null;
		}
	}
}
