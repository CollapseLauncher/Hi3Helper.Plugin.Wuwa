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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management
{
    // Partial declaration that contains the installation helper as a nested type.
    // This file moves the heavy install flow and related download helpers out of the main file.
    internal partial class WuwaGameInstaller
    {
        private sealed class Install
        {
            private readonly WuwaGameInstaller _owner;

            public Install(WuwaGameInstaller owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public async Task RunAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
            {
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Starting installation routine. Mode={Mode}", kind);

                if (_owner.GameAssetBaseUrl is null)
                {
                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] GameAssetBaseUrl is null, aborting.");
                    throw new InvalidOperationException("Game asset base URL is not initialized.");
                }

                if (_owner.GameResourceBasisPath is null)
                {
                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] GameResourceBasisPath is null, aborting.");
                    throw new InvalidOperationException("Game resource basis path is not initialized.");
                }

                if (_owner.ApiResponseAssetUrl is null)
                {
                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] ApiResponseAssetUrl is null, aborting.");
                    throw new InvalidOperationException("Api Response Asset Url is not initialized.");
                }

                // Ensure initialization (loads API/game config)
                await _owner.InitAsync(token).ConfigureAwait(false);

                // Load index (cached)
                WuwaApiResponseResourceIndex? index = await _owner.GetCachedIndexAsync(false, token).ConfigureAwait(false);
                if (index?.Resource == null || index.Resource.Length == 0)
                {
                    SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Cached index empty (entries={Count}). Forcing refresh from: {Url}", index?.Resource.Length ?? -1, _owner.GameAssetBaseUrl);
                    index = await _owner.GetCachedIndexAsync(true, token).ConfigureAwait(false);
                    if (index?.Resource == null || index.Resource.Length == 0)
                    {
                        SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] Resource index is empty even after forced refresh. URL={Url}", _owner.GameAssetBaseUrl);
                        throw new InvalidOperationException("Resource index is empty.");
                    }
                }

    #if DEBUG
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Resource index loaded. Entries: {Count}", index.Resource.Length);
    #endif
                _owner.GameManager.GetGamePath(out string? installPath);
                if (string.IsNullOrEmpty(installPath))
                {
                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] Install path isn't set, aborting.");
                    throw new InvalidOperationException("Game install path isn't set.");
                }

                // Build list of downloadable targets (per-index entry with non-empty dest)
                var entries = index.Resource
                    .Where(e => !string.IsNullOrWhiteSpace(e.Dest))
                    .ToArray();

                // Normalize relative path for each entry (preserve ordering)
                var allTargets = new List<KeyValuePair<string, WuwaApiResponseResourceEntry>>(entries.Length);
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Dest))
                        continue;
                    string relativePath = e.Dest.Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relativePath))
                        continue;
                    allTargets.Add(new KeyValuePair<string, WuwaApiResponseResourceEntry>(relativePath, e));
                }
    #if DEBUG
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Computed total file count from index: {TotalCount}", allTargets.Count);
    #endif
                // If this is an update, filter targets to only those that changed (MD5 mismatch or size mismatch or missing)
                var downloadList = new List<KeyValuePair<string, WuwaApiResponseResourceEntry>>(allTargets.Count);
                int alreadyDownloadedCount = 0;

                foreach (var kv in allTargets)
                {
                    token.ThrowIfCancellationRequested();

                    string rel = kv.Key;
                    var entry = kv.Value;
                    string finalPath = Path.Combine(installPath, rel);

                    // If no file present -> must download
                    if (!File.Exists(finalPath))
                    {
                        downloadList.Add(kv);
                        continue;
                    }

                    // File exists - try to determine if it matches index
                    bool matches = false;
                    try
                    {
                        var fi = new FileInfo(finalPath);
                        // If size present and matches, assume valid (safe fallback when MD5 not available or too large)
                        if (entry.Size > 0 && fi.Length == (long)entry.Size)
                        {
                            matches = true;
                        }

                        // If MD5 present and file small enough, compute and compare
                        if (!matches && !string.IsNullOrEmpty(entry.Md5) && fi.Length <= Md5CheckSizeThreshold)
                        {
                            await using var fs = File.OpenRead(finalPath);
                            string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token);
                            if (string.Equals(md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Error checking existing file {File}: {Err}", finalPath, ex.Message);
                        // If check failed, treat it as not matching to be safe
                    }

                    if (kind == GameInstallerKind.Update && matches)
                    {
                        // For update mode, skip unchanged files
                        alreadyDownloadedCount++;
                        continue;
                    }

                    // Otherwise add to download list
                    downloadList.Add(kv);
                }

                int totalCountToDownload = downloadList.Count;
    #if DEBUG
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Total files to download after filtering (mode={Mode}): {Count}", kind, totalCountToDownload);
    #endif
                // Compute totals (only for downloadList)
                long totalBytesToDownload = 0;
                foreach (var r in downloadList)
                {
                    // clamp/truncate sizes to long safely
                    if (r.Value.Size > (ulong)long.MaxValue)
                        totalBytesToDownload = long.MaxValue;
                    else
                        totalBytesToDownload = checked(totalBytesToDownload + (long)r.Value.Size);
                }

    #if DEBUG
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Total bytes to download (sum of filtered index sizes): {TotalBytes}", totalBytesToDownload);
    #endif
                // Prepare temp folder
                var tempPath = Path.Combine(installPath, "TempPath", "TempGameFiles");
                Directory.CreateDirectory(tempPath);

                // Prepare progress struct with deterministic values
                InstallProgress installProgress = default;
                installProgress.StateCount = 0;
                installProgress.TotalStateToComplete = totalCountToDownload;
                installProgress.DownloadedCount = alreadyDownloadedCount;
                installProgress.TotalCountToDownload = totalCountToDownload;
                installProgress.DownloadedBytes = await _owner.CalculateDownloadedBytesAsync(token).ConfigureAwait(false);
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
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Starting download phase (count={Count})", downloadList.Count);
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

                    // If file already present in temp and valid, skip
                    if (File.Exists(outputPath))
                    {
                        try
                        {
                            var fi = new FileInfo(outputPath);
                            if (entry.Size > 0 && fi.Length == (long)entry.Size)
                            {
                                Interlocked.Increment(ref installProgress.StateCount);
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                                ReportProgress(InstallProgressState.Download);
                                return;
                            }
                        }
                        catch { /* ignore, re-download */ }
                    }

                    // Build original file URI
                    Uri fileUri = new(new Uri(new Uri(_owner.ApiResponseAssetUrl!), _owner.GameResourceBasisPath! + "/"), entry.Dest ?? string.Empty);

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
                        SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] Download failed for {Dest}: {Err}", entry.Dest, ex.Message);
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
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Starting verification phase.");
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
                            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] Size mismatch for {File}: disk={Disk} index={Index}", outputPath, fi.Length, entry.Size);
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
                                SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] MD5 mismatch for {File}: expected={Expected} got={Got}", outputPath, entry.Md5, md5);
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
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Verification error for {File}: {Err}", outputPath, ex.Message);
                        try { File.Delete(outputPath); } catch { }
                        filesToDelete.Add(outputPath);
                    }
                });

                // Retry deleted items once
                if (!filesToDelete.IsEmpty)
                {
                    SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Some files failed verification and were removed; retrying download for missing items.");

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

                            Uri fileUri = new(new Uri(new Uri(_owner.ApiResponseAssetUrl!), _owner.GameResourceBasisPath! + "/"), entry.Dest ?? string.Empty);
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
                                SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::StartInstallCoreAsync] Retry download failed for {Dest}: {Err}", entry.Dest, ex.Message);
                            }
                        });
                    }
                }
                #endregion

                #region Install / Extraction Phase (move temp -> final)
                SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Starting install/extract phase.");
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
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Failed moving file {Temp} -> {Final}: {Err}", tempFile, finalFile, ex.Message);
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
                    _owner.GameManager.GetApiGameVersion(out GameVersion latestVersion);
    #if DEBUG
                    SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] API latest version: {Version}", latestVersion);
    #endif
                    if (latestVersion != GameVersion.Empty)
                    {
                        _owner.GameManager.SetCurrentGameVersion(latestVersion);
                        _owner.GameManager.SaveConfig();
    #if DEBUG
                        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Saved current game version to config.");
    #endif
                    }

                    // Write app-game-config.json
                    try
                    {
                        string configPath = Path.Combine(installPath, "app-game-config.json");
    #if DEBUG
                        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::StartInstallCoreAsync] Writing app-game-config.json to {Path}", configPath);
    #endif
                        using var ms = new MemoryStream();
                        var writerOptions = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                        await using (var writer = new Utf8JsonWriter(ms, writerOptions))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("version", latestVersion == GameVersion.Empty ? string.Empty : latestVersion.ToString());
                            try
                            {
                                var idxName = new Uri(_owner.GameAssetBaseUrl ?? string.Empty, UriKind.Absolute).AbsolutePath;
                                writer.WriteString("indexFile", Path.GetFileName(idxName));
                            }
                            catch { /* ignore */ }
                            writer.WriteEndObject();
                            await writer.FlushAsync(token);
                        }

                        byte[] buffer = ms.ToArray();
                        await File.WriteAllBytesAsync(configPath, buffer, token).ConfigureAwait(false);
                        SharedStatic.InstanceLogger.LogInformation("[WuwaGameInstaller::StartInstallCoreAsync] Wrote app-game-config.json (size={Size})", buffer.Length);
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Failed to write app-game-config.json: {Err}", ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::StartInstallCoreAsync] Post-install actions failed: {Err}", ex.Message);
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

            // The following methods are the download helpers previously in the main file.
            // They use _owner to access HttpClient and configuration.

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
                using (var resp = await _owner._downloadHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
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

                            using HttpResponseMessage resp = await _owner._downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
        }
    }
}