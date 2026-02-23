using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management
{
    // Partial declaration containing the Patch nested class for KRPDiff-based update and preload flows.
    internal partial class WuwaGameInstaller
    {
        private const string PatchTempDirName = "TempPatchFiles";

        /// <summary>
        /// Entry point for the patch flow, used by both update and preload operations.
        /// </summary>
        private Task StartPatchCoreAsync(
            GameInstallerKind kind,
            bool onlyDownload,
            InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate,
            CancellationToken token)
        {
            var patcher = new Patch(this);
            return patcher.RunAsync(kind, onlyDownload, progressDelegate, progressStateDelegate, token);
        }

        /// <summary>
        /// Calculates the total patch download size from the patch index.
        /// Always downloads the actual patch index to compute accurate krpdiff sizes
        /// rather than trusting the config's "size" field, which for older patch entries
        /// represents the full game content size rather than the patch download size.
        /// </summary>
        internal async Task<long> CalculatePatchSizeAsync(GameInstallerKind kind, CancellationToken token)
        {
            var manager = GameManager as WuwaGameManager
                ?? throw new InvalidOperationException("GameManager is not a WuwaGameManager.");

            manager.GetCurrentGameVersion(out GameVersion currentVersion);

            var patchConfig = kind == GameInstallerKind.Preload
                ? manager.GetPreloadPatchConfigForVersion(currentVersion)
                : manager.GetPatchConfigForVersion(currentVersion);

            if (patchConfig == null)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameInstaller::CalculatePatchSizeAsync] No patch config found for version {Version}",
                    currentVersion);
                return 0L;
            }

            // Always download the patch index to compute actual krpdiff sizes.
            // The config "size" field is unreliable — for old-style entries it equals the
            // full game content size, not the patch download size.
            string? patchIndexUrl = BuildPatchIndexUrl(patchConfig);
            if (string.IsNullOrEmpty(patchIndexUrl))
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameInstaller::CalculatePatchSizeAsync] Cannot build patch index URL for version {Version}",
                    currentVersion);
                return 0L;
            }

            var patchIndex = await DownloadPatchIndexAsync(patchIndexUrl, token).ConfigureAwait(false);
            if (patchIndex == null)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameInstaller::CalculatePatchSizeAsync] Failed to download patch index for version {Version}",
                    currentVersion);
                return 0L;
            }

            ulong total = 0;
            int krpCount = 0;
            int fullCount = 0;
            foreach (var entry in patchIndex.Resource)
            {
                if (string.IsNullOrEmpty(entry.Dest))
                    continue;

                if (entry.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase))
                    krpCount++;
                else
                    fullCount++;
            }

            // If there are krpdiff entries, sum only those (small diffs).
            // If there are none (old-style patch), sum the full-replacement entries instead.
            bool hasKrpdiffs = krpCount > 0;
            foreach (var entry in patchIndex.Resource)
            {
                if (string.IsNullOrEmpty(entry.Dest))
                    continue;

                bool isKrpdiff = entry.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase);
                if (hasKrpdiffs ? isKrpdiff : !isKrpdiff)
                    total += entry.Size;
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameInstaller::CalculatePatchSizeAsync] Computed patch size: {Size} bytes — {KrpCount} krpdiff, {FullCount} full-replacement entries (version {Version})",
                total, krpCount, fullCount, currentVersion);

            return total > long.MaxValue ? long.MaxValue : (long)total;
        }

        /// <summary>
        /// Downloads and parses a patch index JSON from the given URL.
        /// Uses manual JsonDocument parsing (same pattern as DownloadResourceIndexAsync)
        /// to handle case-insensitive keys and flexible value types.
        /// </summary>
        internal async Task<WuwaApiResponsePatchIndex?> DownloadPatchIndexAsync(string url, CancellationToken token)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[WuwaGameInstaller::DownloadPatchIndexAsync] Requesting patch index URL: {Url}", url);

            using HttpResponseMessage resp = await _downloadHttpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                string bodyPreview = string.Empty;
                try { bodyPreview = (await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim(); }
                catch { /* ignored */ }

                SharedStatic.InstanceLogger.LogError(
                    "[WuwaGameInstaller::DownloadPatchIndexAsync] GET {Url} returned {Status}. Body preview: {Preview}",
                    url, resp.StatusCode, bodyPreview.Length > 400 ? bodyPreview[..400] + "..." : bodyPreview);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            try
            {
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                JsonElement root = doc.RootElement;

                var result = new WuwaApiResponsePatchIndex();

                // Parse "resource" array
                if (TryGetPropertyCI(root, "resource", out JsonElement resourceElem) &&
                    resourceElem.ValueKind == JsonValueKind.Array)
                {
                    result.Resource = ParseResourceEntries(resourceElem);
                }

                // Parse "deleteFiles" array
                if (TryGetPropertyCI(root, "deleteFiles", out JsonElement deleteElem) &&
                    deleteElem.ValueKind == JsonValueKind.Array)
                {
                    var deleteList = new List<WuwaApiResponsePatchDeleteEntry>(deleteElem.GetArrayLength());
                    foreach (var item in deleteElem.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            deleteList.Add(new WuwaApiResponsePatchDeleteEntry { Dest = item.GetString() });
                        }
                        else if (item.ValueKind == JsonValueKind.Object &&
                                 TryGetPropertyCI(item, "dest", out JsonElement destEl) &&
                                 destEl.ValueKind == JsonValueKind.String)
                        {
                            deleteList.Add(new WuwaApiResponsePatchDeleteEntry { Dest = destEl.GetString() });
                        }
                    }
                    result.DeleteFiles = deleteList.ToArray();
                }

                // Parse "groupInfos" array
                if (TryGetPropertyCI(root, "groupInfos", out JsonElement groupsElem) &&
                    groupsElem.ValueKind == JsonValueKind.Array)
                {
                    var groupList = new List<WuwaApiResponsePatchGroupInfo>(groupsElem.GetArrayLength());
                    foreach (var groupItem in groupsElem.EnumerateArray())
                    {
                        if (groupItem.ValueKind != JsonValueKind.Object)
                            continue;

                        var group = new WuwaApiResponsePatchGroupInfo();

                        if (TryGetPropertyCI(groupItem, "srcFiles", out JsonElement srcElem) &&
                            srcElem.ValueKind == JsonValueKind.Array)
                        {
                            group.SrcFiles = ParseFileRefs(srcElem);
                        }

                        if (TryGetPropertyCI(groupItem, "dstFiles", out JsonElement dstElem) &&
                            dstElem.ValueKind == JsonValueKind.Array)
                        {
                            group.DstFiles = ParseFileRefs(dstElem);
                        }

                        groupList.Add(group);
                    }
                    result.GroupInfos = groupList.ToArray();
                }

                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaGameInstaller::DownloadPatchIndexAsync] Parsed patch index: {ResourceCount} resources, {DeleteCount} deleteFiles, {GroupCount} groupInfos",
                    result.Resource.Length, result.DeleteFiles.Length, result.GroupInfos.Length);
                return result;
            }
            catch (JsonException ex)
            {
                SharedStatic.InstanceLogger.LogError(
                    "[WuwaGameInstaller::DownloadPatchIndexAsync] JSON parse error: {Err}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Builds the patch index URL from a patch config reference.
        /// The IndexFile field is a full relative path from the CDN root
        /// (e.g. "launcher/game/G153/.../indexFile.json"), so we prepend the
        /// CDN base URL (ApiResponseAssetUrl) to form an absolute URI.
        /// </summary>
        private string? BuildPatchIndexUrl(WuwaApiResponseGameConfigRef patchConfig)
        {
            string? indexFile = patchConfig.IndexFile;

            if (string.IsNullOrEmpty(indexFile))
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameInstaller::BuildPatchIndexUrl] PatchConfig has no IndexFile.");
                return null;
            }

            if (!string.IsNullOrEmpty(ApiResponseAssetUrl))
                return $"{ApiResponseAssetUrl.TrimEnd('/')}/{indexFile.TrimStart('/')}";

            return null;
        }

        #region Patch Index Parsing Helpers

        private static WuwaApiResponseResourceEntry[] ParseResourceEntries(JsonElement arrayElem)
        {
            var list = new List<WuwaApiResponseResourceEntry>(arrayElem.GetArrayLength());
            foreach (var item in arrayElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = new WuwaApiResponseResourceEntry();

                if (TryGetPropertyCI(item, "dest", out JsonElement destEl) && destEl.ValueKind == JsonValueKind.String)
                    entry.Dest = destEl.GetString();

                if (TryGetPropertyCI(item, "md5", out JsonElement md5El) && md5El.ValueKind == JsonValueKind.String)
                    entry.Md5 = md5El.GetString();

                if (TryGetPropertyCI(item, "size", out JsonElement sizeEl))
                {
                    if ((sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetUInt64(out ulong uv)) ||
                        (sizeEl.ValueKind == JsonValueKind.String && ulong.TryParse(sizeEl.GetString(), out uv)))
                        entry.Size = uv;
                }

                if (TryGetPropertyCI(item, "chunkInfos", out JsonElement chunksEl) && chunksEl.ValueKind == JsonValueKind.Array)
                {
                    var chunkList = new List<WuwaApiResponseResourceChunkInfo>(chunksEl.GetArrayLength());
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
            return list.ToArray();
        }

        private static WuwaApiResponsePatchFileRef[] ParseFileRefs(JsonElement arrayElem)
        {
            var list = new List<WuwaApiResponsePatchFileRef>(arrayElem.GetArrayLength());
            foreach (var item in arrayElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var fileRef = new WuwaApiResponsePatchFileRef();

                if (TryGetPropertyCI(item, "dest", out JsonElement destEl) && destEl.ValueKind == JsonValueKind.String)
                    fileRef.Dest = destEl.GetString();

                if (TryGetPropertyCI(item, "md5", out JsonElement md5El) && md5El.ValueKind == JsonValueKind.String)
                    fileRef.Md5 = md5El.GetString();

                if (TryGetPropertyCI(item, "size", out JsonElement sizeEl))
                {
                    if ((sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetUInt64(out ulong uv)) ||
                        (sizeEl.ValueKind == JsonValueKind.String && ulong.TryParse(sizeEl.GetString(), out uv)))
                        fileRef.Size = uv;
                }

                list.Add(fileRef);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Case-insensitive JSON property lookup.
        /// </summary>
        private static bool TryGetPropertyCI(JsonElement el, string propName, out JsonElement value)
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

        #endregion

        /// <summary>
        /// Nested class that orchestrates the KRPDiff patch flow.
        /// Mirrors the Install nested class pattern.
        /// </summary>
        private sealed class Patch
        {
            private readonly WuwaGameInstaller _owner;

            public Patch(WuwaGameInstaller owner) => _owner = owner;

            /// <summary>
            /// Main entry point for the patch flow.
            /// </summary>
            /// <param name="kind">Install, Update, or Preload.</param>
            /// <param name="onlyDownload">If true (preload mode), downloads krpdiff files but does not apply them.</param>
            /// <param name="progressDelegate">Progress reporting callback.</param>
            /// <param name="progressStateDelegate">State reporting callback.</param>
            /// <param name="token">Cancellation token.</param>
            public async Task RunAsync(
                GameInstallerKind kind,
                bool onlyDownload,
                InstallProgressDelegate? progressDelegate,
                InstallProgressStateDelegate? progressStateDelegate,
                CancellationToken token)
            {
                var manager = _owner.GameManager as WuwaGameManager
                    ?? throw new InvalidOperationException("GameManager is not a WuwaGameManager.");

                string installPath = _owner.EnsureAndGetGamePath();
                string patchTempPath = Path.Combine(installPath, "TempPath", PatchTempDirName);

                // Initialize progress tracking
                var installProgress = new InstallProgress();
                var currentProgressState = InstallProgressState.Idle;

                void ReportProgress()
                {
                    // Build a snapshot so the host/COM layer sees fully-consistent memory
                    InstallProgress snap = default;
                    snap.StateCount           = Volatile.Read(ref installProgress.StateCount);
                    snap.TotalStateToComplete = Volatile.Read(ref installProgress.TotalStateToComplete);
                    snap.DownloadedCount      = Volatile.Read(ref installProgress.DownloadedCount);
                    snap.TotalCountToDownload = Volatile.Read(ref installProgress.TotalCountToDownload);
                    snap.DownloadedBytes      = Interlocked.Read(ref installProgress.DownloadedBytes);
                    snap.TotalBytesToDownload = Interlocked.Read(ref installProgress.TotalBytesToDownload);

                    progressDelegate?.Invoke(in snap);
                    progressStateDelegate?.Invoke(currentProgressState);
                }

                // ── Step 1: Resolve the correct patch config ──
                currentProgressState = InstallProgressState.Preparing;
                ReportProgress();

                manager.GetCurrentGameVersion(out GameVersion currentVersion);

                WuwaApiResponseGameConfigRef? patchConfig;
                if (kind == GameInstallerKind.Preload)
                    patchConfig = manager.GetPreloadPatchConfigForVersion(currentVersion);
                else
                    patchConfig = manager.GetPatchConfigForVersion(currentVersion);

                if (patchConfig == null)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[Patch::RunAsync] No patch config found for version {Version} (kind={Kind}). " +
                        "Falling back to full install.",
                        currentVersion, kind);
                    // Fall back to the full install flow
                    var installer = new Install(_owner);
                    await installer.RunAsync(kind, progressDelegate, progressStateDelegate, token)
                        .ConfigureAwait(false);
                    return;
                }

                SharedStatic.InstanceLogger.LogInformation(
                    "[Patch::RunAsync] Patch config resolved: from={From} target={Target} baseUrl={BaseUrl}",
                    patchConfig.CurrentVersion,
                    kind == GameInstallerKind.Preload
                        ? manager.ApiPredownloadReference?.CurrentVersion
                        : manager.ApiConfigReference?.CurrentVersion,
                    patchConfig.BaseUrl);

                // ── Step 2: Download and parse the patch index ──
                string? patchIndexUrl = _owner.BuildPatchIndexUrl(patchConfig);
                if (string.IsNullOrEmpty(patchIndexUrl))
                    throw new InvalidOperationException("Cannot construct patch index URL from patch config.");

                var patchIndex = await _owner.DownloadPatchIndexAsync(patchIndexUrl, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Failed to download or parse patch index from {patchIndexUrl}");

                // ── Step 3: Filter .krpdiff entries only ──
                var krpdiffEntries = patchIndex.Resource
                    .Where(e => !string.IsNullOrEmpty(e.Dest) &&
                                e.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                SharedStatic.InstanceLogger.LogInformation(
                    "[Patch::RunAsync] Found {KrpCount} krpdiff files to download out of {TotalCount} resources",
                    krpdiffEntries.Length, patchIndex.Resource.Length);

                // ── Step 3b: Pre-flight validation ──
                // Check whether the installed files already match the TARGET version's hashes.
                // This handles the case where both version JSONs are stale (e.g. game updated
                // externally) but all files on disk are already at the target version.
                if (manager.DEBUG_SkipPreflight)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[Patch::RunAsync] Pre-flight validation SKIPPED (DEBUG_skipPreflight=true). Proceeding to download + patch.");
                }
                else if (!onlyDownload && patchIndex.GroupInfos.Length > 0)
                {
                    currentProgressState = InstallProgressState.Verify;

                    // Count total file pairs and total bytes (via fast metadata stat)
                    // for smooth progress during large-file hashing.
                    int totalPreflightPairs = 0;
                    long totalPreflightBytes = 0;
                    foreach (var g in patchIndex.GroupInfos)
                    {
                        int pairs = Math.Min(g.SrcFiles.Length, g.DstFiles.Length);
                        totalPreflightPairs += pairs;
                        for (int pi = 0; pi < pairs; pi++)
                        {
                            var dst = g.DstFiles[pi];
                            if (string.IsNullOrEmpty(dst.Dest) || string.IsNullOrEmpty(dst.Md5))
                                continue;
                            string p = Path.Combine(installPath,
                                dst.Dest.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(p))
                                totalPreflightBytes += new FileInfo(p).Length;
                        }
                    }

                    // State tracks file count (displayed by host as counter text).
                    // Bytes track hashed data with mid-file granularity for smooth progress.
                    installProgress.TotalCountToDownload = totalPreflightPairs;
                    installProgress.DownloadedCount = 0;
                    installProgress.TotalStateToComplete = totalPreflightPairs;
                    installProgress.StateCount = 0;
                    installProgress.TotalBytesToDownload = totalPreflightBytes;
                    installProgress.DownloadedBytes = 0;
                    ReportProgress();

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Pre-flight validation: checking {FileCount} files across {GroupCount} groups against target hashes...",
                        totalPreflightPairs, patchIndex.GroupInfos.Length);

                    bool allDestinationsMatch = true;
                    int checkedCount = 0;

                    foreach (var group in patchIndex.GroupInfos)
                    {
                        token.ThrowIfCancellationRequested();

                        int pairCount = Math.Min(group.SrcFiles.Length, group.DstFiles.Length);
                        for (int i = 0; i < pairCount; i++)
                        {
                            var dstRef = group.DstFiles[i];
                            if (string.IsNullOrEmpty(dstRef.Dest) || string.IsNullOrEmpty(dstRef.Md5))
                                continue;

                            string dstPath = Path.Combine(installPath,
                                dstRef.Dest.Replace('/', Path.DirectorySeparatorChar));

                            if (!File.Exists(dstPath))
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Pre-flight: file missing, will patch: {File}", dstRef.Dest);
                                allDestinationsMatch = false;
                                break;
                            }

                            // Size check first (cheap) — skip MD5 if size doesn't match
                            var fi = new FileInfo(dstPath);
                            if (dstRef.Size > 0 && (ulong)fi.Length != dstRef.Size)
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Pre-flight: size mismatch for {File} (expected={Expected}, actual={Actual}), will patch.",
                                    dstRef.Dest, dstRef.Size, fi.Length);
                                allDestinationsMatch = false;
                                break;
                            }

                            // MD5 check
                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] Pre-flight: hashing {File} ({Size})...",
                                dstRef.Dest, fi.Length);

                            await using (var fs = File.OpenRead(dstPath))
                            {
                                long hashBytesAccum = 0;
                                const long reportThreshold = 4 << 20; // report every ~4 MiB

                                string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, bytesRead =>
                                {
                                    Interlocked.Add(ref installProgress.DownloadedBytes, bytesRead);
                                    hashBytesAccum += bytesRead;
                                    if (hashBytesAccum >= reportThreshold)
                                    {
                                        ReportProgress();
                                        hashBytesAccum = 0;
                                    }
                                }, token).ConfigureAwait(false);

                                if (!string.Equals(md5, dstRef.Md5, StringComparison.OrdinalIgnoreCase))
                                {
                                    SharedStatic.InstanceLogger.LogDebug(
                                        "[Patch::RunAsync] Pre-flight: MD5 mismatch for {File}, will patch.",
                                        dstRef.Dest);
                                    allDestinationsMatch = false;
                                    break;
                                }
                            }

                            checkedCount++;
                            Interlocked.Increment(ref installProgress.DownloadedCount);
                            Interlocked.Increment(ref installProgress.StateCount);
                            ReportProgress();
                        }

                        if (!allDestinationsMatch)
                            break;
                    }

                    if (allDestinationsMatch && checkedCount > 0)
                    {
                        SharedStatic.InstanceLogger.LogInformation(
                            "[Patch::RunAsync] Pre-flight check: all {Count} destination files already match " +
                            "target version hashes. Files are up-to-date; skipping patch. Updating version only.",
                            checkedCount);

                        // Resolve target version and update
                        GameVersion preflightTargetVer;
                        if (kind == GameInstallerKind.Preload)
                            manager.GetApiPreloadGameVersion(out preflightTargetVer);
                        else
                            manager.GetApiGameVersion(out preflightTargetVer);

                        manager.SetCurrentGameVersion(preflightTargetVer);

                        // Clear DEBUG downgrade flags so the spoofed version doesn't
                        // re-trigger another update cycle on next init/LoadConfig.
                        if (manager.DEBUG_AllowDowngrade)
                        {
                            SharedStatic.InstanceLogger.LogInformation(
                                "[Patch::RunAsync] Clearing DEBUG_AllowDowngrade after successful pre-flight.");
                            manager.DEBUG_AllowDowngrade = false;
                        }

                        manager.SaveConfig();

                        // Clean up any leftover preload temp files
                        try
                        {
                            if (Directory.Exists(patchTempPath))
                                Directory.Delete(patchTempPath, true);
                        }
                        catch { /* best-effort */ }

                        currentProgressState = InstallProgressState.Completed;
                        ReportProgress();
                        return;
                    }

                    SharedStatic.InstanceLogger.LogDebug(
                        "[Patch::RunAsync] Pre-flight check: {Checked} files checked, not all match target. Proceeding with patch.",
                        checkedCount);
                }

                // ── Step 4: Check for pre-downloaded files (preload scenario) ──
                bool hasPredownloadedFiles = false;
                if (!onlyDownload && Directory.Exists(patchTempPath))
                {
                    // Verify ALL expected krpdiff entries exist in the temp directory
                    hasPredownloadedFiles = krpdiffEntries.Length > 0;
                    foreach (var entry in krpdiffEntries)
                    {
                        if (string.IsNullOrEmpty(entry.Dest))
                            continue;

                        string expectedPath = Path.Combine(patchTempPath,
                            entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(expectedPath))
                        {
                            hasPredownloadedFiles = false;
                            SharedStatic.InstanceLogger.LogInformation(
                                "[Patch::RunAsync] Pre-downloaded file missing: {File}. Will re-download all patch files.",
                                entry.Dest);
                            break;
                        }
                    }
                }

                if (hasPredownloadedFiles)
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Using pre-downloaded krpdiff files from {Path} ({Count} files verified)",
                        patchTempPath, krpdiffEntries.Length);
                }

                // ── Step 5: Download patch files ──
                // If krpdiff entries exist, download only those (small diffs applied via groupInfos).
                // If no krpdiff entries exist (old-style patch), download the full replacement entries.
                WuwaApiResponseResourceEntry[] downloadEntries;
                if (krpdiffEntries.Length > 0)
                {
                    downloadEntries = hasPredownloadedFiles
                        ? []   // already pre-downloaded
                        : krpdiffEntries;
                }
                else
                {
                    // Old-style patch: all resources are full replacement files
                    downloadEntries = patchIndex.Resource
                        .Where(e => !string.IsNullOrEmpty(e.Dest))
                        .ToArray();

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] No krpdiff entries found — old-style patch. Downloading {Count} full replacement files.",
                        downloadEntries.Length);
                }

                if (downloadEntries.Length > 0)
                {
                    currentProgressState = InstallProgressState.Download;

                    // Calculate total bytes and set progress
                    ulong totalBytes = 0;
                    foreach (var e in downloadEntries)
                        totalBytes += e.Size;

                    installProgress.TotalBytesToDownload = totalBytes > long.MaxValue ? long.MaxValue : (long)totalBytes;
                    installProgress.TotalCountToDownload = downloadEntries.Length;
                    installProgress.DownloadedBytes = 0;
                    installProgress.DownloadedCount = 0;
                    ReportProgress();

                    // Build the absolute base download URL from the CDN host + patch config's baseUrl
                    string patchRelativeBase = (patchConfig.BaseUrl ?? _owner.GameResourceBasisPath ?? "").TrimEnd('/');
                    string cdnHost = (_owner.ApiResponseAssetUrl ?? "").TrimEnd('/');
                    string patchBaseUrl = string.IsNullOrEmpty(cdnHost)
                        ? patchRelativeBase
                        : $"{cdnHost}/{patchRelativeBase.TrimStart('/')}";

                    Directory.CreateDirectory(patchTempPath);

                    await Parallel.ForEachAsync(downloadEntries,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token },
                        async (entry, ct) =>
                        {
                            if (string.IsNullOrEmpty(entry.Dest))
                                return;

                            string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                            string outputPath = Path.Combine(patchTempPath, relativePath);

                            // Ensure subdirectory exists
                            string? dir = Path.GetDirectoryName(outputPath);
                            if (!string.IsNullOrEmpty(dir))
                                Directory.CreateDirectory(dir);

                            // Build download URL
                            string fileUrl = $"{patchBaseUrl}/{entry.Dest}";
                            Uri uri = new(fileUrl, UriKind.Absolute);

                            Action<long> progressCallback = bytes =>
                            {
                                Interlocked.Add(ref installProgress.DownloadedBytes, bytes);
                                ReportProgress();
                            };

                            if (entry.ChunkInfos is { Length: > 0 })
                            {
                                await _owner.TryDownloadChunkedFileWithFallbacksAsync(
                                    uri, outputPath, entry.ChunkInfos, entry.Dest, ct, progressCallback)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await _owner.TryDownloadWholeFileWithFallbacksAsync(
                                    uri, outputPath, entry.Dest, ct, progressCallback)
                                    .ConfigureAwait(false);
                            }

                            Interlocked.Increment(ref installProgress.DownloadedCount);
                            ReportProgress();
                        }).ConfigureAwait(false);

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Download phase complete. Downloaded {Count} files.",
                        downloadEntries.Length);
                }

                // ── Step 6: Verify downloaded files ──
                currentProgressState = InstallProgressState.Verify;
                ReportProgress();
                foreach (var entry in downloadEntries)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Dest))
                        continue;

                    string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                    string filePath = Path.Combine(patchTempPath, relativePath);

                    if (!File.Exists(filePath))
                    {
                        throw new FileNotFoundException(
                            $"Patch file missing after download: {entry.Dest}", filePath);
                    }

                    var fileInfo = new FileInfo(filePath);
                    if ((ulong)fileInfo.Length != entry.Size)
                    {
                        SharedStatic.InstanceLogger.LogWarning(
                            "[Patch::RunAsync] Size mismatch for {File}: expected={Expected}, actual={Actual}",
                            entry.Dest, entry.Size, fileInfo.Length);
                    }

                    // MD5 verification (skip for large files per existing threshold)
                    if (!string.IsNullOrEmpty(entry.Md5) && fileInfo.Length <= Md5CheckSizeThreshold)
                    {
                        await using var fs = File.OpenRead(filePath);
                        string computedMd5 = await WuwaUtils.ComputeMd5HexAsync(fs, token).ConfigureAwait(false);
                        if (!string.Equals(computedMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"MD5 mismatch for downloaded file {entry.Dest}: expected={entry.Md5}, computed={computedMd5}");
                        }
                    }
                }

                // ── Step 7: If preload only, stop here ──
                if (onlyDownload)
                {
                    // Write a version marker so we can detect staleness later
                    GameVersion targetVersion = kind == GameInstallerKind.Preload
                        ? (manager.ApiPredownloadReference?.CurrentVersion ?? GameVersion.Empty)
                        : (manager.ApiConfigReference?.CurrentVersion ?? GameVersion.Empty);

                    string markerPath = Path.Combine(patchTempPath, ".version");
                    await File.WriteAllTextAsync(markerPath, targetVersion.ToString(), token).ConfigureAwait(false);

                    currentProgressState = InstallProgressState.Completed;
                    ReportProgress();
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Preload download complete. Files saved to {Path}. Target version: {Version}",
                        patchTempPath, targetVersion);
                    return;
                }

                // ── Step 8: Delete files from deleteFiles list ──
                if (patchIndex.DeleteFiles.Length > 0)
                {
                    currentProgressState = InstallProgressState.Removing;
                    ReportProgress();
                    foreach (var deleteEntry in patchIndex.DeleteFiles)
                    {
                        if (string.IsNullOrEmpty(deleteEntry.Dest))
                            continue;

                        string filePath = Path.Combine(installPath,
                            deleteEntry.Dest.Replace('/', Path.DirectorySeparatorChar));

                        if (File.Exists(filePath))
                        {
                            try
                            {
                                File.Delete(filePath);
                                SharedStatic.InstanceLogger.LogDebug("[Patch::RunAsync] Deleted file: {Path}", filePath);
                            }
                            catch (Exception ex)
                            {
                                SharedStatic.InstanceLogger.LogWarning(
                                    "[Patch::RunAsync] Failed to delete {Path}: {Err}", filePath, ex.Message);
                            }
                        }
                    }
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Processed {Count} delete entries.", patchIndex.DeleteFiles.Length);
                }

                // ── Step 9: Apply patches from groupInfos ──
                if (patchIndex.GroupInfos.Length > 0)
                {
                    currentProgressState = InstallProgressState.Updating;

                    // Count total file pairs across all groups for accurate progress tracking
                    int totalFilePairs = 0;
                    foreach (var g in patchIndex.GroupInfos)
                        totalFilePairs += Math.Min(g.SrcFiles.Length, g.DstFiles.Length);

                    installProgress.TotalStateToComplete = totalFilePairs;
                    installProgress.StateCount = 0;
                    ReportProgress();

                    string progressMarkerPath = Path.Combine(patchTempPath, ".patch_progress");
                    int currentPairIndex = 0;

                    foreach (var group in patchIndex.GroupInfos)
                    {
                        token.ThrowIfCancellationRequested();

                        if (group.SrcFiles.Length == 0 || group.DstFiles.Length == 0)
                            continue;

                        int pairCount = Math.Min(group.SrcFiles.Length, group.DstFiles.Length);

                        for (int pairIdx = 0; pairIdx < pairCount; pairIdx++)
                        {
                            token.ThrowIfCancellationRequested();

                            var srcRef = group.SrcFiles[pairIdx];
                            var dstRef = group.DstFiles[pairIdx];

                            if (string.IsNullOrEmpty(srcRef.Dest) || string.IsNullOrEmpty(dstRef.Dest))
                            {
                                currentPairIndex++;
                                continue;
                            }

                            string srcPath = Path.Combine(installPath,
                                srcRef.Dest.Replace('/', Path.DirectorySeparatorChar));
                            string dstPath = Path.Combine(installPath,
                                dstRef.Dest.Replace('/', Path.DirectorySeparatorChar));
                            string tempOutPath = dstPath + ".patching";

                            // State-based resume: if destination already has correct target MD5, skip
                            if (!string.IsNullOrEmpty(dstRef.Md5) && File.Exists(dstPath))
                            {
                                await using var existingStream = File.OpenRead(dstPath);
                                string existingMd5 = await WuwaUtils.ComputeMd5HexAsync(existingStream, token)
                                    .ConfigureAwait(false);
                                if (string.Equals(existingMd5, dstRef.Md5, StringComparison.OrdinalIgnoreCase))
                                {
                                    SharedStatic.InstanceLogger.LogDebug(
                                        "[Patch::RunAsync] Destination already matches target MD5, skipping: {Dst}",
                                        dstRef.Dest);
                                    currentPairIndex++;
                                    Interlocked.Increment(ref installProgress.StateCount);
                                    ReportProgress();
                                    await File.WriteAllTextAsync(progressMarkerPath,
                                        currentPairIndex.ToString(), token).ConfigureAwait(false);
                                    continue;
                                }
                            }

                            // Find the corresponding krpdiff file
                            string krpdiffPath = FindKrpdiffFile(patchTempPath, dstRef.Dest, krpdiffEntries);

                            // Pre-patch validation: verify source file MD5
                            if (!string.IsNullOrEmpty(srcRef.Md5) && File.Exists(srcPath))
                            {
                                var srcInfo = new FileInfo(srcPath);
                                if (srcInfo.Length <= Md5CheckSizeThreshold)
                                {
                                    await using var srcStream = File.OpenRead(srcPath);
                                    string srcMd5 = await WuwaUtils.ComputeMd5HexAsync(srcStream, token).ConfigureAwait(false);
                                    if (!string.Equals(srcMd5, srcRef.Md5, StringComparison.OrdinalIgnoreCase))
                                    {
                                        SharedStatic.InstanceLogger.LogWarning(
                                            "[Patch::RunAsync] Source MD5 mismatch for {File}: expected={Expected}, actual={Actual}",
                                            srcRef.Dest, srcRef.Md5, srcMd5);
                                    }
                                }
                            }

                            // Ensure output directory exists
                            string? outDir = Path.GetDirectoryName(tempOutPath);
                            if (!string.IsNullOrEmpty(outDir))
                                Directory.CreateDirectory(outDir);

                            // Apply the HDiff patch
                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] Applying patch: src={Src}, diff={Diff}, out={Out}",
                                srcPath, krpdiffPath, tempOutPath);

                            HPatchZNative.ApplyPatch(srcPath, krpdiffPath, tempOutPath, token);

                            // Post-patch validation: verify output file MD5 (no size threshold - always verify)
                            if (!string.IsNullOrEmpty(dstRef.Md5))
                            {
                                await using var outStream = File.OpenRead(tempOutPath);
                                string outMd5 = await WuwaUtils.ComputeMd5HexAsync(outStream, token).ConfigureAwait(false);
                                if (!string.Equals(outMd5, dstRef.Md5, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException(
                                        $"Post-patch MD5 mismatch for {dstRef.Dest}: expected={dstRef.Md5}, computed={outMd5}");
                                }
                            }

                            // Move patched file to final location
                            if (File.Exists(dstPath))
                                File.Delete(dstPath);
                            File.Move(tempOutPath, dstPath);

                            // If src and dst are different paths, clean up old source
                            if (!string.Equals(srcRef.Dest, dstRef.Dest, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(srcPath))
                            {
                                try { File.Delete(srcPath); }
                                catch { /* ignore */ }
                            }

                            currentPairIndex++;
                            Interlocked.Increment(ref installProgress.StateCount);
                            ReportProgress();

                            // Write progress marker for resume support
                            await File.WriteAllTextAsync(progressMarkerPath, currentPairIndex.ToString(), token)
                                .ConfigureAwait(false);

                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] Patch applied: {Src} -> {Dst}", srcRef.Dest, dstRef.Dest);
                        }
                    }

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Applied {Count} file pairs across {GroupCount} groups.",
                        currentPairIndex, patchIndex.GroupInfos.Length);
                }

                // ── Step 10: Also handle non-krpdiff resource files (full replacement files) ──
                var fullReplacementEntries = patchIndex.Resource
                    .Where(e => !string.IsNullOrEmpty(e.Dest) &&
                                !e.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (fullReplacementEntries.Length > 0)
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] {Count} full replacement files to move.", fullReplacementEntries.Length);

                    foreach (var entry in fullReplacementEntries)
                    {
                        if (string.IsNullOrEmpty(entry.Dest))
                            continue;

                        string srcInTemp = Path.Combine(patchTempPath,
                            entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                        string destInInstall = Path.Combine(installPath,
                            entry.Dest.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(srcInTemp))
                            continue;

                        string? destDir = Path.GetDirectoryName(destInInstall);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        if (File.Exists(destInInstall))
                            File.Delete(destInInstall);
                        File.Move(srcInTemp, destInInstall);
                    }
                }

                // ── Step 11: Cleanup and update version ──
                try
                {
                    if (Directory.Exists(patchTempPath))
                        Directory.Delete(patchTempPath, true);
                    SharedStatic.InstanceLogger.LogDebug(
                        "[Patch::RunAsync] Cleaned up patch temp directory: {Path}", patchTempPath);
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[Patch::RunAsync] Failed to clean up patch temp: {Err}", ex.Message);
                }

                // Update game version to the target version
                GameVersion targetVer;
                if (kind == GameInstallerKind.Preload)
                {
                    manager.GetApiPreloadGameVersion(out targetVer);
                }
                else
                {
                    manager.GetApiGameVersion(out targetVer);
                }

                manager.SetCurrentGameVersion(targetVer);

                // Clear DEBUG downgrade flags so the spoofed version doesn't re-trigger
                // another update cycle on next init.
                if (manager.DEBUG_AllowDowngrade)
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Clearing DEBUG_AllowDowngrade after successful patch.");
                    manager.DEBUG_AllowDowngrade = false;
                }

                manager.SaveConfig();

                currentProgressState = InstallProgressState.Completed;
                ReportProgress();
                SharedStatic.InstanceLogger.LogInformation(
                    "[Patch::RunAsync] Patch complete. Game updated to version {Version}.", targetVer);
            }

            /// <summary>
            /// Finds the krpdiff file corresponding to a destination file reference.
            /// Tries several naming conventions.
            /// </summary>
            private static string FindKrpdiffFile(
                string patchTempPath,
                string dstDest,
                WuwaApiResponseResourceEntry[] krpdiffEntries)
            {
                // Strategy 1: Look for a krpdiff entry whose dest matches dstDest + ".krpdiff"
                string expectedKrpdiff = dstDest + ".krpdiff";
                var matchingEntry = krpdiffEntries.FirstOrDefault(
                    e => string.Equals(e.Dest, expectedKrpdiff, StringComparison.OrdinalIgnoreCase));

                if (matchingEntry != null && !string.IsNullOrEmpty(matchingEntry.Dest))
                {
                    string path = Path.Combine(patchTempPath,
                        matchingEntry.Dest.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                        return path;
                }

                // Strategy 2: Look for krpdiff file with matching base name
                string baseName = Path.GetFileNameWithoutExtension(dstDest);
                foreach (var entry in krpdiffEntries)
                {
                    if (string.IsNullOrEmpty(entry.Dest))
                        continue;

                    string entryBaseName = Path.GetFileNameWithoutExtension(
                        entry.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase)
                            ? entry.Dest[..^".krpdiff".Length]
                            : entry.Dest);

                    if (string.Equals(baseName, entryBaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = Path.Combine(patchTempPath,
                            entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path))
                            return path;
                    }
                }

                // Strategy 3: If only one krpdiff entry exists, use it
                if (krpdiffEntries.Length == 1 && !string.IsNullOrEmpty(krpdiffEntries[0].Dest))
                {
                    string path = Path.Combine(patchTempPath,
                        krpdiffEntries[0].Dest!.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                        return path;
                }

                throw new FileNotFoundException(
                    $"Cannot find krpdiff file for destination: {dstDest}");
            }
        }
    }
}
