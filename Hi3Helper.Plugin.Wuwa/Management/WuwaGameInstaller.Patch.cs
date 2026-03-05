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
        private const string PreflightStateFileName = ".preflight_state";

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

            // Sum ALL resource entries: krpdiffs + full-replacement entries.
            // When krpdiffs exist, full-replacement entries are additional files that also
            // need downloading (e.g. new files not covered by any group diff). In old-style
            // mode (no krpdiffs), only the full-replacement entries are summed.
            foreach (var entry in patchIndex.Resource)
            {
                if (string.IsNullOrEmpty(entry.Dest))
                    continue;

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

                int lastLoggedDownloadedCount = -1;

                void ReportProgress()
                {
                    try
                    {
                        // Build a snapshot so the host/COM layer sees fully-consistent memory
                        InstallProgress snap = default;
                        snap.StateCount           = Volatile.Read(ref installProgress.StateCount);
                        snap.TotalStateToComplete = Volatile.Read(ref installProgress.TotalStateToComplete);
                        snap.DownloadedCount      = Volatile.Read(ref installProgress.DownloadedCount);
                        snap.TotalCountToDownload = Volatile.Read(ref installProgress.TotalCountToDownload);
                        snap.DownloadedBytes      = Interlocked.Read(ref installProgress.DownloadedBytes);
                        snap.TotalBytesToDownload = Interlocked.Read(ref installProgress.TotalBytesToDownload);

                        int prev = Interlocked.Exchange(ref lastLoggedDownloadedCount, snap.DownloadedCount);
                        if (prev != snap.DownloadedCount)
                        {
                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::ReportProgress] State={State}, Bytes={DownloadedBytes}/{TotalBytes}, " +
                                "Count={DownloadedCount}/{TotalCount}, Files={StateCount}/{TotalState}",
                                currentProgressState,
                                snap.DownloadedBytes, snap.TotalBytesToDownload,
                                snap.DownloadedCount, snap.TotalCountToDownload,
                                snap.StateCount, snap.TotalStateToComplete);
                        }

                        progressDelegate?.Invoke(in snap);
                        progressStateDelegate?.Invoke(currentProgressState);

                        // The host-side adapter (PluginGameInstallWrapper) applies a
                        // 100 ms refresh-window check with *inverted* logic: the first
                        // call after a >100 ms gap resets the internal timer and is
                        // silently discarded.  A second, immediate invocation always
                        // falls inside the window and actually triggers the UI progress
                        // update (ProgressChanged event).  During the download phase the
                        // HTTP callbacks fire fast enough that some naturally slip
                        // through, but during the install/apply phase callbacks are
                        // spaced far apart and ALL get swallowed without this workaround.
                        progressDelegate?.Invoke(in snap);
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogWarning(
                            "[Patch::ReportProgress] Failed to invoke progress delegate: {Err}", ex.Message);
                    }
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
                // When pre-flight finds mismatches, this set tracks WHICH dst files don't match
                // so only their corresponding krpdiffs need to be downloaded (not the full set).
                //
                // Resume support: verification results are persisted incrementally to a
                // .preflight_state file. If the user cancels mid-verify and restarts,
                // already-verified files are loaded from the state file and skipped.
                HashSet<string>? mismatchedDstFiles = null;

                if (manager.DEBUG_SkipPreflight)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[Patch::RunAsync] Pre-flight validation SKIPPED (DEBUG_skipPreflight=true). Proceeding to download + patch.");
                }
                else if (!onlyDownload && patchIndex.GroupInfos.Length > 0)
                {
                    currentProgressState = InstallProgressState.Verify;

                    // ── Load previous pre-flight state (resume support) ──
                    // File format: line 1 = patchIndexUrl (staleness key),
                    //              subsequent lines = "M\t<dest>" (match) or "X\t<dest>" (mismatch).
                    Directory.CreateDirectory(patchTempPath);
                    string preflightStatePath = Path.Combine(patchTempPath, PreflightStateFileName);
                    var previouslyVerified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                    if (File.Exists(preflightStatePath))
                    {
                        try
                        {
                            string[] stateLines = await File.ReadAllLinesAsync(preflightStatePath, token)
                                .ConfigureAwait(false);
                            if (stateLines.Length > 0 &&
                                string.Equals(stateLines[0], patchIndexUrl, StringComparison.Ordinal))
                            {
                                for (int li = 1; li < stateLines.Length; li++)
                                {
                                    string line = stateLines[li];
                                    if (line.Length < 3) continue; // minimum: "M\tx"
                                    char tag = line[0];
                                    string dest = line[2..]; // skip tag + tab
                                    previouslyVerified[dest] = tag == 'X'; // true = mismatch
                                }

                                SharedStatic.InstanceLogger.LogInformation(
                                    "[Patch::RunAsync] Resuming pre-flight: loaded {Count} previously verified files from state file.",
                                    previouslyVerified.Count);
                            }
                            else
                            {
                                SharedStatic.InstanceLogger.LogInformation(
                                    "[Patch::RunAsync] Pre-flight state file is stale (different patch index URL). Starting fresh.");
                                File.Delete(preflightStatePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            SharedStatic.InstanceLogger.LogWarning(
                                "[Patch::RunAsync] Failed to read pre-flight state file, starting fresh: {Err}", ex.Message);
                            try { File.Delete(preflightStatePath); } catch { /* best-effort */ }
                        }
                    }

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
                            // For previously verified files, don't count their bytes
                            // (they'll be "instant" during the loop)
                            if (previouslyVerified.ContainsKey(dst.Dest))
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
                        "[Patch::RunAsync] Pre-flight validation: checking {FileCount} files across {GroupCount} groups ({ResumedCount} already verified, {BytesToHash} bytes to hash)...",
                        totalPreflightPairs, patchIndex.GroupInfos.Length,
                        previouslyVerified.Count, totalPreflightBytes);

                    mismatchedDstFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int checkedCount = 0;

                    // Open the state file for incremental appending.
                    // If we're starting fresh, write the header first.
                    await using var preflightStateWriter = new StreamWriter(
                        preflightStatePath, append: previouslyVerified.Count > 0);

                    if (previouslyVerified.Count == 0)
                    {
                        await preflightStateWriter.WriteLineAsync(patchIndexUrl).ConfigureAwait(false);
                        await preflightStateWriter.FlushAsync(token).ConfigureAwait(false);
                    }

                    foreach (var group in patchIndex.GroupInfos)
                    {
                        token.ThrowIfCancellationRequested();

                        int pairCount = Math.Min(group.SrcFiles.Length, group.DstFiles.Length);
                        for (int i = 0; i < pairCount; i++)
                        {
                            var dstRef = group.DstFiles[i];
                            if (string.IsNullOrEmpty(dstRef.Dest) || string.IsNullOrEmpty(dstRef.Md5))
                                continue;

                            // ── Resume: use cached result if this file was verified previously ──
                            if (previouslyVerified.TryGetValue(dstRef.Dest, out bool wasMismatch))
                            {
                                if (wasMismatch)
                                    mismatchedDstFiles.Add(dstRef.Dest);
                                checkedCount++;
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                                Interlocked.Increment(ref installProgress.StateCount);
                                ReportProgress();
                                continue;
                            }

                            string dstPath = Path.Combine(installPath,
                                dstRef.Dest.Replace('/', Path.DirectorySeparatorChar));

                            if (!File.Exists(dstPath))
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Pre-flight: file missing, will patch: {File}", dstRef.Dest);
                                mismatchedDstFiles.Add(dstRef.Dest);
                                await preflightStateWriter.WriteLineAsync($"X\t{dstRef.Dest}").ConfigureAwait(false);
                                await preflightStateWriter.FlushAsync(token).ConfigureAwait(false);
                                checkedCount++;
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                                Interlocked.Increment(ref installProgress.StateCount);
                                ReportProgress();
                                continue;
                            }

                            // Size check first (cheap) — skip MD5 if size doesn't match
                            var fi = new FileInfo(dstPath);
                            if (dstRef.Size > 0 && (ulong)fi.Length != dstRef.Size)
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Pre-flight: size mismatch for {File} (expected={Expected}, actual={Actual}), will patch.",
                                    dstRef.Dest, dstRef.Size, fi.Length);
                                mismatchedDstFiles.Add(dstRef.Dest);
                                await preflightStateWriter.WriteLineAsync($"X\t{dstRef.Dest}").ConfigureAwait(false);
                                await preflightStateWriter.FlushAsync(token).ConfigureAwait(false);
                                // Account for skipped bytes so progress bar stays accurate
                                Interlocked.Add(ref installProgress.DownloadedBytes, fi.Length);
                                checkedCount++;
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                                Interlocked.Increment(ref installProgress.StateCount);
                                ReportProgress();
                                continue;
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
                                    mismatchedDstFiles.Add(dstRef.Dest);
                                    await preflightStateWriter.WriteLineAsync($"X\t{dstRef.Dest}").ConfigureAwait(false);
                                }
                                else
                                {
                                    await preflightStateWriter.WriteLineAsync($"M\t{dstRef.Dest}").ConfigureAwait(false);
                                }

                                await preflightStateWriter.FlushAsync(token).ConfigureAwait(false);
                            }

                            checkedCount++;
                            Interlocked.Increment(ref installProgress.DownloadedCount);
                            Interlocked.Increment(ref installProgress.StateCount);
                            ReportProgress();
                        }
                    }

                    if (mismatchedDstFiles.Count == 0 && checkedCount > 0)
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

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Pre-flight check: {Mismatched} of {Checked} files need patching.",
                        mismatchedDstFiles.Count, checkedCount);
                }

                // ── Step 3c: Filter krpdiff entries to only the files that need patching ──
                // Krpdiff filenames are group-based (e.g. "3.0.3_3.1.0_group_N_timestamp.krpdiff")
                // and do NOT match dest file paths. We use GroupInfos to map: for each group,
                // check if any of its DstFiles are in mismatchedDstFiles; if so, we need that
                // group's krpdiff.
                WuwaApiResponseResourceEntry[] krpdiffEntriesToDownload;
                if (mismatchedDstFiles is { Count: > 0 } && krpdiffEntries.Length > 0)
                {
                    // Build group-index → krpdiff entry lookup from krpdiff filenames
                    var groupToKrpdiffDest = new Dictionary<int, string>();
                    foreach (var entry in krpdiffEntries)
                    {
                        if (string.IsNullOrEmpty(entry.Dest)) continue;
                        int gIdx = ParseGroupIndex(entry.Dest);
                        if (gIdx >= 0)
                            groupToKrpdiffDest[gIdx] = entry.Dest;
                    }

                    // For each GroupInfo, check if any of its DstFiles need patching
                    var neededKrpdiffDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int gi = 0; gi < patchIndex.GroupInfos.Length; gi++)
                    {
                        var group = patchIndex.GroupInfos[gi];
                        bool groupNeeded = false;
                        foreach (var d in group.DstFiles)
                        {
                            if (!string.IsNullOrEmpty(d.Dest) && mismatchedDstFiles.Contains(d.Dest))
                            {
                                groupNeeded = true;
                                break;
                            }
                        }

                        if (groupNeeded && groupToKrpdiffDest.TryGetValue(gi, out string? krpDest))
                            neededKrpdiffDests.Add(krpDest);
                    }

                    if (neededKrpdiffDests.Count > 0)
                    {
                        krpdiffEntriesToDownload = krpdiffEntries
                            .Where(e => !string.IsNullOrEmpty(e.Dest) && neededKrpdiffDests.Contains(e.Dest))
                            .ToArray();
                    }
                    else
                    {
                        // Group index parsing didn't match — fall back to downloading all krpdiffs
                        SharedStatic.InstanceLogger.LogWarning(
                            "[Patch::RunAsync] Could not map mismatched files to group krpdiffs. Downloading all {Total} krpdiffs as fallback.",
                            krpdiffEntries.Length);
                        krpdiffEntriesToDownload = krpdiffEntries;
                    }

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Filtered downloads: {Filtered} of {Total} krpdiffs needed based on pre-flight ({Mismatched} mismatched files).",
                        krpdiffEntriesToDownload.Length, krpdiffEntries.Length, mismatchedDstFiles.Count);
                }
                else if (mismatchedDstFiles is { Count: 0 })
                {
                    // Pre-flight found zero mismatches — nothing to download
                    // (this should have been caught by the early return above, but just in case)
                    krpdiffEntriesToDownload = [];
                }
                else
                {
                    // Pre-flight didn't run (skipped / onlyDownload / no groupInfos) — download all
                    krpdiffEntriesToDownload = krpdiffEntries;
                }

                // ── Step 4: Check for pre-downloaded files (preload scenario) ──
                bool hasPredownloadedFiles = false;
                if (!onlyDownload && Directory.Exists(patchTempPath))
                {
                    // Verify all NEEDED krpdiff entries exist in the temp directory
                    hasPredownloadedFiles = krpdiffEntriesToDownload.Length > 0;
                    foreach (var entry in krpdiffEntriesToDownload)
                    {
                        if (string.IsNullOrEmpty(entry.Dest))
                            continue;

                        string expectedPath = Path.Combine(patchTempPath,
                            entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(expectedPath))
                        {
                            hasPredownloadedFiles = false;
                            SharedStatic.InstanceLogger.LogInformation(
                                "[Patch::RunAsync] Pre-downloaded file missing: {File}. Will re-download needed patch files.",
                                entry.Dest);
                            break;
                        }
                    }
                }

                if (hasPredownloadedFiles)
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Using pre-downloaded krpdiff files from {Path} ({Count} files verified)",
                        patchTempPath, krpdiffEntriesToDownload.Length);
                }

                // ── Step 5: Download patch files ──
                // Use the ORIGINAL krpdiffEntries.Length to distinguish "has krpdiff-based patching"
                // from "old-style full replacement". The filtered set (krpdiffEntriesToDownload)
                // may be empty when pre-flight determined all files are up-to-date.
                WuwaApiResponseResourceEntry[] downloadEntries;
                if (krpdiffEntries.Length > 0)
                {
                    // Group-based patch mode: download only needed krpdiffs
                    if (krpdiffEntriesToDownload.Length > 0)
                    {
                        downloadEntries = hasPredownloadedFiles
                            ? []   // already pre-downloaded
                            : krpdiffEntriesToDownload;
                    }
                    else
                    {
                        // All files already up-to-date (or pre-flight filtered everything out)
                        downloadEntries = [];
                    }

                    // Also check for full-replacement entries (non-krpdiff resources) that need downloading.
                    // These are files not covered by any group (e.g. new files added in the target version).
                    var fullReplacementToDownload = patchIndex.Resource
                        .Where(e => !string.IsNullOrEmpty(e.Dest) &&
                                    !e.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (fullReplacementToDownload.Length > 0)
                    {
                        SharedStatic.InstanceLogger.LogInformation(
                            "[Patch::RunAsync] {Count} full-replacement entries in patch index (will download alongside krpdiffs).",
                            fullReplacementToDownload.Length);

                        // Merge full-replacement entries into download list
                        if (downloadEntries.Length > 0)
                        {
                            var merged = new WuwaApiResponseResourceEntry[downloadEntries.Length + fullReplacementToDownload.Length];
                            downloadEntries.CopyTo(merged, 0);
                            fullReplacementToDownload.CopyTo(merged, downloadEntries.Length);
                            downloadEntries = merged;
                        }
                        else if (mismatchedDstFiles is null or { Count: > 0 })
                        {
                            // Pre-flight didn't run or found mismatches — download full-replacement entries
                            downloadEntries = fullReplacementToDownload;
                        }
                    }
                }
                else
                {
                    // Old-style patch: NO krpdiff entries in the patch index at all.
                    // All resources are full replacement files.
                    downloadEntries = patchIndex.Resource
                        .Where(e => !string.IsNullOrEmpty(e.Dest))
                        .ToArray();

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] No krpdiff entries in patch index — old-style patch. Downloading {Count} full replacement files.",
                        downloadEntries.Length);

                    // Log sample entries for diagnostics
                    for (int si = 0; si < Math.Min(5, downloadEntries.Length); si++)
                    {
                        var sample = downloadEntries[si];
                        SharedStatic.InstanceLogger.LogDebug(
                            "[Patch::RunAsync] Sample resource[{Idx}]: dest={Dest}, size={Size}, chunks={Chunks}",
                            si, sample.Dest, sample.Size, sample.ChunkInfos?.Length ?? 0);
                    }
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

                    // Build the absolute base download URLs:
                    // - patchBaseUrl: for krpdiff entries (from patchConfig.BaseUrl)
                    // - mainBaseUrl:  for full-replacement entries (from main game config's BaseUrl)
                    string cdnHost = (_owner.ApiResponseAssetUrl ?? "").TrimEnd('/');

                    string patchRelativeBase = (patchConfig.BaseUrl ?? _owner.GameResourceBasisPath ?? "").TrimEnd('/');
                    string patchBaseUrl = string.IsNullOrEmpty(cdnHost)
                        ? patchRelativeBase
                        : $"{cdnHost}/{patchRelativeBase.TrimStart('/')}";

                    string mainRelativeBase = (_owner.GameResourceBasisPath ?? "").TrimEnd('/');
                    string mainBaseUrl = string.IsNullOrEmpty(cdnHost)
                        ? mainRelativeBase
                        : $"{cdnHost}/{mainRelativeBase.TrimStart('/')}";

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Download base URL (patch): {PatchUrl}", patchBaseUrl);
                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Download base URL (main): {MainUrl}", mainBaseUrl);

                    Directory.CreateDirectory(patchTempPath);

                    await Parallel.ForEachAsync(downloadEntries,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token },
                        async (entry, ct) =>
                        {
                            if (string.IsNullOrEmpty(entry.Dest))
                                return;

                            bool isKrpdiff = entry.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase);
                            string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                            string outputPath = Path.Combine(patchTempPath, relativePath);

                            // For full-replacement entries, check if the file already exists in the
                            // game install directory with the correct size — skip download if so.
                            if (!isKrpdiff)
                            {
                                string existingPath = Path.Combine(installPath, relativePath);
                                if (File.Exists(existingPath))
                                {
                                    var fi = new FileInfo(existingPath);
                                    if (fi.Length == (long)entry.Size)
                                    {
                                        SharedStatic.InstanceLogger.LogDebug(
                                            "[Patch::RunAsync] Full-replacement file already exists with correct size, skipping: {Dest}",
                                            entry.Dest);
                                        Interlocked.Add(ref installProgress.DownloadedBytes, fi.Length);
                                        Interlocked.Increment(ref installProgress.DownloadedCount);
                                        ReportProgress();
                                        return;
                                    }
                                }
                            }

                            // Ensure subdirectory exists
                            string? dir = Path.GetDirectoryName(outputPath);
                            if (!string.IsNullOrEmpty(dir))
                                Directory.CreateDirectory(dir);

                            // Build download URL: krpdiff files use the patch CDN,
                            // full-replacement files use the main game resource CDN.
                            string baseUrl = isKrpdiff ? patchBaseUrl : mainBaseUrl;
                            string fileUrl = $"{baseUrl}/{entry.Dest}";
                            Uri uri = new(fileUrl, UriKind.Absolute);

                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] Downloading: {Url}", fileUrl);

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

                    bool isKrpdiff = entry.Dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase);
                    string relativePath = entry.Dest.Replace('/', Path.DirectorySeparatorChar);
                    string filePath = Path.Combine(patchTempPath, relativePath);

                    // Full-replacement files may have been skipped during download because
                    // they already exist in the install directory with the correct size.
                    // Check both temp and install locations.
                    if (!File.Exists(filePath) && !isKrpdiff)
                    {
                        string installFilePath = Path.Combine(installPath, relativePath);
                        if (File.Exists(installFilePath))
                        {
                            var installFi = new FileInfo(installFilePath);
                            if (installFi.Length == (long)entry.Size)
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Verification: full-replacement file verified in install dir (skipped download): {Dest}",
                                    entry.Dest);
                                continue;
                            }
                        }
                    }

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

                // ── Step 8: Apply patches from groupInfos ──
                // NOTE: Deletions (old Step 8) are deferred until AFTER patching because
                // directory-level krpdiffs reference old source files that must still
                // exist on disk when PatchDir reads them.
                // Each krpdiff is a DIRECTORY-LEVEL diff — it patches an entire group of
                // files at once.  We must apply it once per group with the game install
                // directory as the source, NOT per individual file pair.
                //
                // Build the CDN base URL for fallback full-replacement downloads.
                // If any source file is missing for a group, we download the destination
                // files directly from the CDN instead of applying the krpdiff.
                string fallbackCdnHost = (_owner.ApiResponseAssetUrl ?? "").TrimEnd('/');
                string fallbackRelativeBase = (_owner.GameResourceBasisPath ?? "").TrimEnd('/');
                string fallbackBaseUrl = string.IsNullOrEmpty(fallbackCdnHost)
                    ? fallbackRelativeBase
                    : $"{fallbackCdnHost}/{fallbackRelativeBase.TrimStart('/')}";

                if (patchIndex.GroupInfos.Length > 0)
                {
                    currentProgressState = InstallProgressState.Install;

                    // Count total destination files and bytes across all groups
                    int totalDstFiles = 0;
                    long totalPatchBytes = 0;
                    foreach (var g in patchIndex.GroupInfos)
                    {
                        totalDstFiles += g.DstFiles.Length;
                        foreach (var d in g.DstFiles)
                            totalPatchBytes += (long)d.Size;
                    }

                    installProgress.TotalStateToComplete = totalDstFiles;
                    installProgress.StateCount = 0;
                    installProgress.TotalBytesToDownload = totalPatchBytes;
                    installProgress.TotalCountToDownload = totalDstFiles;
                    installProgress.DownloadedBytes = 0;
                    installProgress.DownloadedCount = 0;
                    ReportProgress();

                    int completedGroups = 0;
                    long cumulativeExpectedBytes = 0; // exact byte total after each completed group

                    for (int groupIdx = 0; groupIdx < patchIndex.GroupInfos.Length; groupIdx++)
                    {
                        token.ThrowIfCancellationRequested();

                        var group = patchIndex.GroupInfos[groupIdx];
                        if (group.DstFiles.Length == 0)
                            continue;

                        // Pre-compute expected byte total for this group
                        long groupExpectedBytes = 0;
                        foreach (var d in group.DstFiles)
                            groupExpectedBytes += (long)d.Size;

                        // ─ Resume check: if ALL destination files already have the
                        //   correct size + MD5, the group was fully applied previously. ─
                        bool allDstMatch = true;
                        foreach (var dstRef in group.DstFiles)
                        {
                            if (string.IsNullOrEmpty(dstRef.Dest) || string.IsNullOrEmpty(dstRef.Md5))
                                continue;

                            string dstPath = Path.Combine(installPath,
                                dstRef.Dest.Replace('/', Path.DirectorySeparatorChar));

                            if (!File.Exists(dstPath))
                            { allDstMatch = false; break; }

                            var fi = new FileInfo(dstPath);
                            if (fi.Length != (long)dstRef.Size)
                            { allDstMatch = false; break; }

                            await using var existingStream = File.OpenRead(dstPath);
                            string existingMd5 = await WuwaUtils
                                .ComputeMd5HexAsync(existingStream, token)
                                .ConfigureAwait(false);
                            if (!string.Equals(existingMd5, dstRef.Md5,
                                    StringComparison.OrdinalIgnoreCase))
                            { allDstMatch = false; break; }
                        }

                        if (allDstMatch)
                        {
                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] All destination files for group {Idx} already match, skipping.",
                                groupIdx);
                            cumulativeExpectedBytes += groupExpectedBytes;
                            Interlocked.Exchange(ref installProgress.DownloadedBytes, cumulativeExpectedBytes);
                            foreach (var dstRef in group.DstFiles)
                            {
                                Interlocked.Increment(ref installProgress.StateCount);
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                            }
                            ReportProgress();
                            completedGroups++;
                            continue;
                        }

                        // ─ Find the krpdiff for this group ─
                        string krpdiffPath = FindKrpdiffFile(
                            patchTempPath,
                            group.DstFiles[0].Dest ?? "",
                            krpdiffEntries,
                            groupIdx);

                        // ─ Pre-check: verify all source files exist ─
                        // Directory-level krpdiffs need the old source files on disk.
                        // If any are missing (corrupted install), download the target
                        // destination files directly from the CDN as full replacements
                        // instead of trying to patch.
                        var missingSrcFiles = new List<string>();
                        foreach (var srcRef in group.SrcFiles)
                        {
                            if (string.IsNullOrEmpty(srcRef.Dest)) continue;
                            string srcPath = Path.Combine(installPath,
                                srcRef.Dest.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(srcPath))
                                missingSrcFiles.Add(srcRef.Dest);
                        }

                        if (missingSrcFiles.Count > 0)
                        {
                            SharedStatic.InstanceLogger.LogWarning(
                                "[Patch::RunAsync] Group {Idx}: {Count} source file(s) missing — " +
                                "downloading destination files directly as full replacement.",
                                groupIdx, missingSrcFiles.Count);
                            foreach (var m in missingSrcFiles)
                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync]   Missing source: {File}", m);

                            // Download each destination file from CDN
                            foreach (var dstRef in group.DstFiles)
                            {
                                token.ThrowIfCancellationRequested();
                                if (string.IsNullOrEmpty(dstRef.Dest)) continue;

                                string dstRelative = dstRef.Dest.Replace('/', Path.DirectorySeparatorChar);
                                string finalDst = Path.Combine(installPath, dstRelative);

                                // If destination already matches target, skip download
                                if (File.Exists(finalDst))
                                {
                                    var existFi = new FileInfo(finalDst);
                                    if (existFi.Length == (long)dstRef.Size &&
                                        !string.IsNullOrEmpty(dstRef.Md5))
                                    {
                                        await using var existStream = File.OpenRead(finalDst);
                                        string existMd5 = await WuwaUtils
                                            .ComputeMd5HexAsync(existStream, token)
                                            .ConfigureAwait(false);
                                        if (string.Equals(existMd5, dstRef.Md5,
                                                StringComparison.OrdinalIgnoreCase))
                                        {
                                            SharedStatic.InstanceLogger.LogDebug(
                                                "[Patch::RunAsync] Dest already at target, skip download: {Dst}",
                                                dstRef.Dest);
                                            Interlocked.Increment(ref installProgress.StateCount);
                                            Interlocked.Add(ref installProgress.DownloadedBytes, (long)dstRef.Size);
                                            Interlocked.Increment(ref installProgress.DownloadedCount);
                                            ReportProgress();
                                            continue;
                                        }
                                    }
                                }

                                string fileUrl = $"{fallbackBaseUrl}/{dstRef.Dest}";
                                Uri uri = new(fileUrl, UriKind.Absolute);

                                // Ensure directory exists
                                string? dstDir = Path.GetDirectoryName(finalDst);
                                if (!string.IsNullOrEmpty(dstDir))
                                    Directory.CreateDirectory(dstDir);

                                SharedStatic.InstanceLogger.LogDebug(
                                    "[Patch::RunAsync] Downloading replacement: {Url}", fileUrl);

                                await _owner.TryDownloadWholeFileWithFallbacksAsync(
                                    uri, finalDst, dstRef.Dest, token,
                                    bytes =>
                                    {
                                        Interlocked.Add(ref installProgress.DownloadedBytes, bytes);
                                        ReportProgress();
                                    }).ConfigureAwait(false);

                                // Post-download MD5 verification
                                if (!string.IsNullOrEmpty(dstRef.Md5))
                                {
                                    await using var dlStream = File.OpenRead(finalDst);
                                    string dlMd5 = await WuwaUtils
                                        .ComputeMd5HexAsync(dlStream, token)
                                        .ConfigureAwait(false);
                                    if (!string.Equals(dlMd5, dstRef.Md5,
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        throw new InvalidOperationException(
                                            $"Downloaded replacement file MD5 mismatch for " +
                                            $"{dstRef.Dest}: expected={dstRef.Md5}, computed={dlMd5}");
                                    }
                                }

                                Interlocked.Increment(ref installProgress.StateCount);
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                                ReportProgress();
                            }

                            cumulativeExpectedBytes += groupExpectedBytes;
                            Interlocked.Exchange(ref installProgress.DownloadedBytes, cumulativeExpectedBytes);
                            ReportProgress();
                            completedGroups++;
                            continue;
                        }

                        // ─ Apply the krpdiff as a directory-level patch ─
                        // Source = game install root (contains old files at their relative paths).
                        // Output = per-group temp dir so we can verify before committing.
                        string tempGroupDir = Path.Combine(patchTempPath, $"_patch_group_{groupIdx}");

                        SharedStatic.InstanceLogger.LogDebug(
                            "[Patch::RunAsync] Applying group {Idx} dir patch: srcDir={Src}, diff={Diff}, outDir={Out}",
                            groupIdx, installPath, krpdiffPath, tempGroupDir);

                        try
                        {
                            // Use writeBytesDelegate for real-time byte progress during
                            // the (blocking) patch operation so the UI stays responsive.
                            long patchBytesAccum = 0;
                            const long patchReportThreshold = 4 << 20; // ~4 MiB

                            HPatchZNative.ApplyDirPatch(installPath, krpdiffPath, tempGroupDir,
                                writeBytesDelegate: bytesWritten =>
                                {
                                    Interlocked.Add(ref installProgress.DownloadedBytes, bytesWritten);
                                    patchBytesAccum += bytesWritten;
                                    if (patchBytesAccum >= patchReportThreshold)
                                    {
                                        ReportProgress();
                                        patchBytesAccum = 0;
                                    }
                                }, token: token);

                            // Final flush in case last chunk was below threshold
                            if (patchBytesAccum > 0)
                                ReportProgress();
                        }
                        catch (Exception patchEx) when (patchEx is not OperationCanceledException)
                        {
                            // Patching failed (e.g. missing source file).
                            // Check whether ALL destination files already match the target
                            // hashes — if so, this group was effectively already applied
                            // and we can skip it safely. Otherwise, re-throw.
                            SharedStatic.InstanceLogger.LogWarning(
                                "[Patch::RunAsync] Patch failed for group {Idx}: {Err}. " +
                                "Checking if destination files already match target...",
                                groupIdx, patchEx.Message);

                            bool allDstMatchFallback = true;
                            foreach (var dstCheck in group.DstFiles)
                            {
                                if (string.IsNullOrEmpty(dstCheck.Dest) ||
                                    string.IsNullOrEmpty(dstCheck.Md5))
                                    continue;

                                string dstCheckPath = Path.Combine(installPath,
                                    dstCheck.Dest.Replace('/', Path.DirectorySeparatorChar));

                                if (!File.Exists(dstCheckPath))
                                { allDstMatchFallback = false; break; }

                                var dstCheckFi = new FileInfo(dstCheckPath);
                                if (dstCheckFi.Length != (long)dstCheck.Size)
                                { allDstMatchFallback = false; break; }

                                await using var dstCheckStream = File.OpenRead(dstCheckPath);
                                string dstCheckMd5 = await WuwaUtils
                                    .ComputeMd5HexAsync(dstCheckStream, token)
                                    .ConfigureAwait(false);
                                if (!string.Equals(dstCheckMd5, dstCheck.Md5,
                                        StringComparison.OrdinalIgnoreCase))
                                { allDstMatchFallback = false; break; }
                            }

                            // Clean up any partial output from the failed attempt
                            try
                            {
                                if (Directory.Exists(tempGroupDir))
                                    Directory.Delete(tempGroupDir, true);
                            }
                            catch { /* ignore */ }

                            if (!allDstMatchFallback)
                            {
                                // Destination files don't match — this is a real failure
                                throw new InvalidOperationException(
                                    $"Patch application failed for group {groupIdx} and " +
                                    $"destination files do not match target hashes. " +
                                    $"The game installation may be corrupted.", patchEx);
                            }

                            SharedStatic.InstanceLogger.LogInformation(
                                "[Patch::RunAsync] Group {Idx}: patch failed but all destination " +
                                "files already match target — skipping (already applied).",
                                groupIdx);
                            cumulativeExpectedBytes += groupExpectedBytes;
                            Interlocked.Exchange(ref installProgress.DownloadedBytes, cumulativeExpectedBytes);
                            foreach (var dstRef in group.DstFiles)
                            {
                                Interlocked.Increment(ref installProgress.StateCount);
                                Interlocked.Increment(ref installProgress.DownloadedCount);
                            }
                            ReportProgress();
                            completedGroups++;
                            continue;
                        }

                        // ─ Verify each output file and move to final location ─
                        foreach (var dstRef in group.DstFiles)
                        {
                            if (string.IsNullOrEmpty(dstRef.Dest))
                                continue;

                            string relativePath = dstRef.Dest.Replace('/', Path.DirectorySeparatorChar);
                            string patchedFile = Path.Combine(tempGroupDir, relativePath);
                            string finalDst    = Path.Combine(installPath, relativePath);

                            if (!File.Exists(patchedFile))
                            {
                                throw new FileNotFoundException(
                                    $"Expected patched output file not found after dir patch " +
                                    $"(group {groupIdx}): {dstRef.Dest}",
                                    patchedFile);
                            }

                            // Post-patch MD5 verification
                            if (!string.IsNullOrEmpty(dstRef.Md5))
                            {
                                await using var outStream = File.OpenRead(patchedFile);
                                string outMd5 = await WuwaUtils
                                    .ComputeMd5HexAsync(outStream, token)
                                    .ConfigureAwait(false);
                                if (!string.Equals(outMd5, dstRef.Md5,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException(
                                        $"Post-patch MD5 mismatch for {dstRef.Dest}: " +
                                        $"expected={dstRef.Md5}, computed={outMd5}");
                                }
                            }

                            // Move verified file to install directory
                            string? destDir = Path.GetDirectoryName(finalDst);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);

                            if (File.Exists(finalDst))
                                File.Delete(finalDst);
                            File.Move(patchedFile, finalDst);

                            Interlocked.Increment(ref installProgress.StateCount);
                            Interlocked.Add(ref installProgress.DownloadedBytes, (long)dstRef.Size);
                            Interlocked.Increment(ref installProgress.DownloadedCount);
                            ReportProgress();

                            SharedStatic.InstanceLogger.LogDebug(
                                "[Patch::RunAsync] Moved patched file: {Dst}", dstRef.Dest);
                        }

                        // Snap DownloadedBytes to exact expected value after the group
                        // so the byte counter aligns precisely with the sum of dstFile
                        // sizes (corrects any discrepancy from HDiff write callbacks).
                        cumulativeExpectedBytes += groupExpectedBytes;
                        Interlocked.Exchange(ref installProgress.DownloadedBytes, cumulativeExpectedBytes);
                        ReportProgress();

                        // Clean up the per-group temp directory
                        try
                        {
                            if (Directory.Exists(tempGroupDir))
                                Directory.Delete(tempGroupDir, true);
                        }
                        catch { /* ignore cleanup errors */ }

                        completedGroups++;
                        SharedStatic.InstanceLogger.LogDebug(
                            "[Patch::RunAsync] Completed group {Idx}: {Count} files patched.",
                            groupIdx, group.DstFiles.Length);
                    }

                    SharedStatic.InstanceLogger.LogInformation(
                        "[Patch::RunAsync] Applied patches across {CompletedGroups}/{GroupCount} groups.",
                        completedGroups, patchIndex.GroupInfos.Length);
                }

                // ── Step 9: Delete files from deleteFiles list ──
                // This runs AFTER patching so that directory-level krpdiffs can still
                // read the old source files they reference.
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

                // Write LocalGameResources.json so Kuro's launcher recognises the updated files
                try
                {
                    var resourceIndex = await _owner.GetCachedIndexAsync(false, token).ConfigureAwait(false);
                    if (resourceIndex != null)
                        manager.TryWriteLocalGameResources(installPath, resourceIndex);
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[Patch::RunAsync] Failed to write LocalGameResources.json: {Err}", ex.Message);
                }

                currentProgressState = InstallProgressState.Completed;
                ReportProgress();
                SharedStatic.InstanceLogger.LogInformation(
                    "[Patch::RunAsync] Patch complete. Game updated to version {Version}.", targetVer);
            }

            /// <summary>
            /// Finds the krpdiff file corresponding to a destination file reference.
            /// Tries several naming conventions.
            /// </summary>
            /// <summary>
            /// Parses the group index N from a krpdiff filename like "X.X.X_Y.Y.Y_group_N_timestamp.krpdiff".
            /// Returns -1 if the pattern is not found.
            /// </summary>
            private static int ParseGroupIndex(string dest)
            {
                const string marker = "_group_";
                int pos = dest.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) return -1;
                int numStart = pos + marker.Length;
                int numEnd = dest.IndexOf('_', numStart);
                if (numEnd < 0) return -1;
                return int.TryParse(dest.AsSpan(numStart, numEnd - numStart), out int idx) ? idx : -1;
            }

            private static string FindKrpdiffFile(
                string patchTempPath,
                string dstDest,
                WuwaApiResponseResourceEntry[] krpdiffEntries,
                int groupIndex = -1)
            {
                // Strategy 0 (preferred): Find krpdiff by matching group index in its filename
                if (groupIndex >= 0)
                {
                    foreach (var entry in krpdiffEntries)
                    {
                        if (string.IsNullOrEmpty(entry.Dest)) continue;
                        if (ParseGroupIndex(entry.Dest) == groupIndex)
                        {
                            string path = Path.Combine(patchTempPath,
                                entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(path))
                                return path;
                        }
                    }
                }

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
                    $"Cannot find krpdiff file for destination: {dstDest} (groupIndex={groupIndex})");
            }
        }
    }
}
