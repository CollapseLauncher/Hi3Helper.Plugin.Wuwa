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

		// Load index (cached)
		WuwaApiResponseResourceIndex? index = await GetCachedIndexAsync(false, token).ConfigureAwait(false);
		if (index?.Resource == null || index.Resource.Length == 0)
		{
			SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Cached index empty (entries={Count}). Forcing refresh from: {Url}", index?.Resource.Length ?? -1, GameAssetBaseUrl);
			index = await GetCachedIndexAsync(true, token).ConfigureAwait(false);
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

		// Compute totals
		long totalBytesToDownload = 0;
		foreach (var r in index.Resource)
			totalBytesToDownload += (long)r.Size;

		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Total bytes to download (sum of index sizes): {TotalBytes}", totalBytesToDownload);

		// Build list of downloadable targets (per-index entry with non-empty dest)
		var entries = index.Resource
			.Where(e => !string.IsNullOrWhiteSpace(e.Dest))
			.ToArray();

		// Normalize relative path for each entry (preserve ordering)
		var downloadList = new List<KeyValuePair<string, WuwaApiResponseResourceEntry>>(entries.Length);
		foreach (var e in entries)
		{
			if (string.IsNullOrWhiteSpace(e.Dest))
				continue;
			string relativePath = e.Dest.Replace('/', Path.DirectorySeparatorChar)
				.TrimStart(Path.DirectorySeparatorChar);
			if (string.IsNullOrWhiteSpace(relativePath))
				continue;
			downloadList.Add(new KeyValuePair<string, WuwaApiResponseResourceEntry>(relativePath, e));
		}

		int totalCountToDownload = downloadList.Count;
		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Computed total file count to download: {TotalCount}", totalCountToDownload);

		// Seed downloaded counts by checking files already present and valid
		var seededPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int alreadyDownloadedCount = 0;
		try
		{
			foreach (var kv in downloadList)
			{
				token.ThrowIfCancellationRequested();

				string rel = kv.Key;
				var entry = kv.Value;
				string outputPath = Path.Combine(installPath, rel);

				if (!File.Exists(outputPath))
					continue;

				try
				{
					var fi = new FileInfo(outputPath);

					if (entry.Size > 0)
					{
						if (fi.Length == (long)entry.Size)
						{
							alreadyDownloadedCount++;
							seededPaths.Add(outputPath);
							continue;
						}
					}

					if (!string.IsNullOrEmpty(entry.Md5) && fi.Length <= Md5CheckSizeThreshold)
					{
						await using var fs = File.OpenRead(outputPath);
						string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token);
						if (string.Equals(md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
						{
							alreadyDownloadedCount++;
							seededPaths.Add(outputPath);
						}
					}
				}
				catch (Exception ex)
				{
					SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Error while seeding file {Path}: {Err}", outputPath, ex.Message);
				}
			}
		}
		catch (OperationCanceledException) { }

		SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Seeding counts: TotalCountToDownload={TotalCount}, AlreadyDownloadedCount={Already}, IndexEntries={IndexEntries}",
			totalCountToDownload, alreadyDownloadedCount, index.Resource.Length);

		// Prepare temp folder
		var tempPath = Path.Combine(installPath, "TempPath", "TempGameFiles");
		Directory.CreateDirectory(tempPath);

		// Prepare progress struct with deterministic values
		InstallProgress installProgress = default;
		installProgress.StateCount = 0;
		installProgress.TotalStateToComplete = totalCountToDownload;
		installProgress.DownloadedCount = alreadyDownloadedCount;
		installProgress.TotalCountToDownload = totalCountToDownload;
		installProgress.DownloadedBytes = await CalculateDownloadedBytesAsync(token).ConfigureAwait(false);
		installProgress.TotalBytesToDownload = totalBytesToDownload;

		int lastLoggedDownloadedCount = -1;
		void ReportProgress(InstallProgressState currentState)
		{
			try
			{
				// Build a fresh snapshot struct so marshalling/host sees fully-initialized memory
				InstallProgress snap = default;
				snap.StateCount = Volatile.Read(ref installProgress.StateCount);
				snap.TotalStateToComplete = Volatile.Read(ref installProgress.TotalStateToComplete);
				snap.DownloadedCount = Volatile.Read(ref installProgress.DownloadedCount);
				snap.TotalCountToDownload = Volatile.Read(ref installProgress.TotalCountToDownload);
				snap.DownloadedBytes = Interlocked.Read(ref installProgress.DownloadedBytes);
				snap.TotalBytesToDownload = Interlocked.Read(ref installProgress.TotalBytesToDownload);

				int prev = Interlocked.Exchange(ref lastLoggedDownloadedCount, snap.DownloadedCount);
				if (prev != snap.DownloadedCount)
				{
					SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::ReportProgress] Sending progress snapshot: DownloadedCount={DownloadedCount}/{TotalCount}, DownloadedBytes={DownloadedBytes}/{TotalBytes}, State={StateCount}/{TotalState}",
						snap.DownloadedCount, snap.TotalCountToDownload, snap.DownloadedBytes, snap.TotalBytesToDownload, snap.StateCount, snap.TotalStateToComplete);
				}

#pragma warning disable CS0618
				try
				{
					// No-op placeholder for additional alias assignment if needed later
				}
				catch { }
#pragma warning restore CS0618

				progressDelegate?.Invoke(in snap);
                progressStateDelegate?.Invoke(currentState);
            }
			catch (Exception ex)
			{
				SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::ReportProgress] Failed to invoke progress delegate: {Err}", ex.Message);
				// swallow to avoid crashing the installer
			}
		}

        // Send initial update
        ReportProgress(InstallProgressState.Preparing);
		
		// Collections for verification/cleanup
		var filesToDelete = new ConcurrentBag<string>();

		// Helper to report byte increments (thread-safe)
		void DownloadBytesCallback(long delta)
		{
			if (delta == 0) return;
			Interlocked.Add(ref installProgress.DownloadedBytes, delta);
			ReportProgress(InstallProgressState.Download);
        }

		#region Download Implementation
		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Starting download phase (count={Count})", downloadList.Count);
		try { progressStateDelegate?.Invoke(InstallProgressState.Download); } catch { }

		await Parallel.ForEachAsync(downloadList, new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
			CancellationToken = token
		}, async (kv, innerToken) =>
		{
			string rel = kv.Key;
			var entry = kv.Value;
			string outputPath = Path.Combine(tempPath, rel);
			string? parentDir = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(parentDir))
				Directory.CreateDirectory(parentDir);

			// If file already present and valid, skip
			if (File.Exists(outputPath))
			{
				try
				{
					var fi = new FileInfo(outputPath);
					if (entry.Size > 0 && fi.Length == (long)entry.Size)
					{
						Interlocked.Increment(ref installProgress.StateCount);
						if (seededPaths.Add(outputPath))
							Interlocked.Increment(ref installProgress.DownloadedCount);

						ReportProgress(InstallProgressState.Download);
						return;
					}
				}
				catch { /* ignore, re-download */ }
			}

			// Build original file URI
			Uri fileUri = new(new Uri(new Uri(ApiResponseAssetUrl!), GameResourceBasisPath! + "/"), entry.Dest ?? string.Empty);

			// Download (choose chunked or whole)
			try
			{
				if (entry.ChunkInfos == null || entry.ChunkInfos.Length == 0)
				{
					await TryDownloadWholeFileWithFallbacksAsync(fileUri, outputPath, entry.Dest ?? string.Empty, innerToken, DownloadBytesCallback).ConfigureAwait(false);
				}
				else
				{
					await TryDownloadChunkedFileWithFallbacksAsync(fileUri, outputPath, entry.ChunkInfos, entry.Dest ?? string.Empty, innerToken, DownloadBytesCallback).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Download failed for {Dest}: {Err}", entry.Dest, ex.Message);
				filesToDelete.Add(outputPath);
				return;
			}

			// After download, increment state counter (verification step will validate integrity)
			Interlocked.Increment(ref installProgress.StateCount);
			Interlocked.Increment(ref installProgress.DownloadedCount);
			ReportProgress(InstallProgressState.Download);
		});
		#endregion

		#region Verification Phase
		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Starting verification phase.");
		Volatile.Write(ref installProgress.StateCount, 0);
		Volatile.Write(ref installProgress.DownloadedCount, 0);
		Interlocked.Exchange(ref installProgress.DownloadedBytes, 0L);
		ReportProgress(InstallProgressState.Verify);

		await Parallel.ForEachAsync(downloadList, new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
			CancellationToken = token
		}, async (kv, innerToken) =>
		{
			string rel = kv.Key;
			var entry = kv.Value;
			string outputPath = Path.Combine(tempPath, rel);

			// If file missing skip (it was marked for deletion)
			if (!File.Exists(outputPath))
			{
				filesToDelete.Add(outputPath);
				return;
			}

			try
			{
				var fi = new FileInfo(outputPath);
				if (entry.Size > 0 && fi.Length != (long)entry.Size)
				{
					SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Size mismatch for {File}: disk={Disk} index={Index}", outputPath, fi.Length, entry.Size);
					try { File.Delete(outputPath); } catch { }
					filesToDelete.Add(outputPath);
					return;
				}

				if (!string.IsNullOrEmpty(entry.Md5) && fi.Length <= Md5CheckSizeThreshold)
				{
					await using var fs = File.OpenRead(outputPath);
					string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, innerToken);
					if (!string.Equals(md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
					{
						SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] MD5 mismatch for {File}: expected={Expected} got={Got}", outputPath, entry.Md5, md5);
						try { File.Delete(outputPath); } catch { }
						filesToDelete.Add(outputPath);
						return;
					}
				}

				// Verified successfully
				Interlocked.Increment(ref installProgress.StateCount);
				Interlocked.Increment(ref installProgress.DownloadedCount);
				if (fi.Exists)
					Interlocked.Add(ref installProgress.DownloadedBytes, fi.Length);

				ReportProgress(InstallProgressState.Verify);
			}
			catch (Exception ex)
			{
				SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Verification error for {File}: {Err}", outputPath, ex.Message);
				try { File.Delete(outputPath); } catch { }
				filesToDelete.Add(outputPath);
			}
		});

		// Retry deleted items once
		if (!filesToDelete.IsEmpty)
		{
			SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Some files failed verification and were removed; retrying download for missing items.");

			var retryList = downloadList.Where(kv => !File.Exists(Path.Combine(tempPath, kv.Key))).ToArray();
			if (retryList.Length > 0)
			{
				try { progressStateDelegate?.Invoke(InstallProgressState.Download); } catch { }
				await Parallel.ForEachAsync(retryList, new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
					CancellationToken = token
				}, async (kv, innerToken) =>
				{
					string rel = kv.Key;
					var entry = kv.Value;
					string outputPath = Path.Combine(tempPath, rel);
					string? parentDir = Path.GetDirectoryName(outputPath);
					if (!string.IsNullOrEmpty(parentDir))
						Directory.CreateDirectory(parentDir);

					Uri fileUri = new(new Uri(new Uri(ApiResponseAssetUrl!), GameResourceBasisPath! + "/"), entry.Dest ?? string.Empty);
					try
					{
						if (entry.ChunkInfos == null || entry.ChunkInfos.Length == 0)
							await TryDownloadWholeFileWithFallbacksAsync(fileUri, outputPath, entry.Dest ?? string.Empty, innerToken, DownloadBytesCallback).ConfigureAwait(false);
						else
							await TryDownloadChunkedFileWithFallbacksAsync(fileUri, outputPath, entry.ChunkInfos, entry.Dest ?? string.Empty, innerToken, DownloadBytesCallback).ConfigureAwait(false);

						Interlocked.Increment(ref installProgress.StateCount);
						Interlocked.Increment(ref installProgress.DownloadedCount);
						ReportProgress(InstallProgressState.Download);
					}
					catch (Exception ex)
					{
						SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallAsyncInner] Retry download failed for {Dest}: {Err}", entry.Dest, ex.Message);
					}
				});
			}
		}
		#endregion

		#region Install / Extraction Phase (move temp -> final)
		SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallAsyncInner] Starting install/extract phase.");
		Volatile.Write(ref installProgress.StateCount, 0);
		Volatile.Write(ref installProgress.DownloadedCount, 0);
		Interlocked.Exchange(ref installProgress.DownloadedBytes, 0L);
		ReportProgress(InstallProgressState.Install);

		foreach (var kv in downloadList)
		{
			token.ThrowIfCancellationRequested();
			string rel = kv.Key;
			string tempFile = Path.Combine(tempPath, rel);
			string finalFile = Path.Combine(installPath, rel);
			string? parentDir = Path.GetDirectoryName(finalFile);
			if (!string.IsNullOrEmpty(parentDir))
				Directory.CreateDirectory(parentDir);

			try
			{
				if (File.Exists(tempFile))
				{
					// Overwrite final file
					if (File.Exists(finalFile))
						File.Delete(finalFile);
					File.Move(tempFile, finalFile);
					var fi = new FileInfo(finalFile);
					Interlocked.Add(ref installProgress.DownloadedBytes, fi.Length);
				}
			}
			catch (Exception ex)
			{
				SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallAsyncInner] Failed moving file {Temp} -> {Final}: {Err}", tempFile, finalFile, ex.Message);
			}

			Interlocked.Increment(ref installProgress.StateCount);
			Interlocked.Increment(ref installProgress.DownloadedCount);
			ReportProgress(InstallProgressState.Install);
		}

		// Cleanup temp directory if empty
		try
		{
			if (Directory.Exists(tempPath))
				Directory.Delete(tempPath, true);
		}
		catch { /* ignore cleanup errors */ }
		#endregion

		#region Post-install actions routines
		try
		{
			GameManager.GetApiGameVersion(out GameVersion latestVersion);
			SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] API latest version: {Version}", latestVersion);
			if (latestVersion != GameVersion.Empty)
			{
				GameManager.SetCurrentGameVersion(latestVersion);
				GameManager.SaveConfig();
				SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallAsyncInner] Saved current game version to config.");
			}

			// Write app-game-config.json
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
					try
					{
						var idxName = new Uri(GameAssetBaseUrl ?? string.Empty, UriKind.Absolute).AbsolutePath;
						writer.WriteString("indexFile", Path.GetFileName(idxName));
					}
					catch { /* ignore */ }
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

		// Final progress update
		try
		{
			progressStateDelegate?.Invoke(InstallProgressState.Completed);
			// clamp downloaded bytes
			long totalBytes = Interlocked.Read(ref installProgress.TotalBytesToDownload);
			long downloaded = Interlocked.Read(ref installProgress.DownloadedBytes);
			if (totalBytes > 0 && downloaded > totalBytes)
				Interlocked.Exchange(ref installProgress.DownloadedBytes, totalBytes);
			ReportProgress(InstallProgressState.Install);
		}
		catch
		{
			// swallow
		}
		#endregion
	}

	protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // reuse install routine for now (could filter resources)
        return StartInstallAsyncInner(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // reuse install routine (will overwrite or skip existing files)
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

	#region Helpers
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
