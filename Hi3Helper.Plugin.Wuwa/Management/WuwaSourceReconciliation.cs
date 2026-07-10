using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

/// <summary>
/// Verifies installed source-version files against Kuro CDN manifests and repairs
/// mismatches by re-downloading canonical files before krpdiff apply.
/// </summary>
internal static class WuwaSourceReconciliation
{
    internal readonly record struct ReconcileResult(
        int Checked,
        int Repaired,
        int SkippedUnrepairable,
        int AlreadyMatched);

    internal static Dictionary<string, WuwaApiResponseResourceEntry> BuildResourceLookup(
        WuwaApiResponseResourceIndex? index)
    {
        var lookup = new Dictionary<string, WuwaApiResponseResourceEntry>(StringComparer.OrdinalIgnoreCase);
        if (index?.Resource == null)
            return lookup;

        foreach (var entry in index.Resource)
        {
            if (string.IsNullOrEmpty(entry.Dest))
                continue;
            lookup.TryAdd(entry.Dest, entry);
        }

        return lookup;
    }

    internal static Dictionary<string, WuwaApiResponsePatchFileRef> CollectSourceRefs(
        WuwaApiResponsePatchIndex patchIndex)
    {
        var refs = new Dictionary<string, WuwaApiResponsePatchFileRef>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in patchIndex.GroupInfos)
        {
            foreach (var src in group.SrcFiles)
            {
                if (string.IsNullOrEmpty(src.Dest))
                    continue;
                refs.TryAdd(src.Dest, src);
            }
        }

        return refs;
    }

    /// <summary>
    /// Maps each source file path to its paired destination reference in the patch index.
    /// </summary>
    internal static Dictionary<string, WuwaApiResponsePatchFileRef> CollectDestinationRefsForSources(
        WuwaApiResponsePatchIndex patchIndex)
    {
        var refs = new Dictionary<string, WuwaApiResponsePatchFileRef>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in patchIndex.GroupInfos)
        {
            int pairCount = Math.Min(group.SrcFiles.Length, group.DstFiles.Length);
            for (int i = 0; i < pairCount; i++)
            {
                WuwaApiResponsePatchFileRef src = group.SrcFiles[i];
                WuwaApiResponsePatchFileRef dst = group.DstFiles[i];
                if (string.IsNullOrEmpty(src.Dest))
                    continue;
                refs.TryAdd(src.Dest, dst);
            }
        }

        return refs;
    }

    /// <summary>
    /// Compares patch source files on disk to CDN manifests and downloads canonical
    /// source-version replacements when hashes or sizes do not match.
    /// </summary>
    internal static async Task<ReconcileResult> ReconcileSourceFilesAsync(
        string installPath,
        WuwaApiResponsePatchIndex patchIndex,
        IReadOnlyDictionary<string, WuwaApiResponseResourceEntry> resourceLookup,
        string sourceCdnHost,
        string sourceRelativeBase,
        WuwaGameInstaller installer,
        Action<string, long, int>? onFileStarted,
        Action<long>? reportBytes,
        Action? reportProgress,
        Action? reportFileCompleted,
        Action<long>? addToTotalBytes,
        CancellationToken token)
    {
        var sourceRefs = CollectSourceRefs(patchIndex);
        if (sourceRefs.Count == 0)
            return new ReconcileResult(0, 0, 0, 0);

        var destinationRefs = CollectDestinationRefsForSources(patchIndex);

        string cdnHost = sourceCdnHost.TrimEnd('/');
        string baseUrl = string.IsNullOrEmpty(cdnHost)
            ? sourceRelativeBase.TrimEnd('/')
            : $"{cdnHost}/{sourceRelativeBase.TrimStart('/').TrimEnd('/')}";

        int checkedCount = 0;
        int repairedCount = 0;
        int skippedCount = 0;
        int alreadyMatchedCount = 0;
        long hashBytesAccum = 0;
        const long hashReportThreshold = 256 << 10;

        void ReportHashBytes(long bytesRead)
        {
            reportBytes?.Invoke(bytesRead);
            hashBytesAccum += bytesRead;
            if (hashBytesAccum >= hashReportThreshold)
            {
                hashBytesAccum = 0;
                reportProgress?.Invoke();
            }
        }

        foreach (var (dest, srcRef) in sourceRefs)
        {
            token.ThrowIfCancellationRequested();
            checkedCount++;

            resourceLookup.TryGetValue(dest, out WuwaApiResponseResourceEntry? resourceEntry);

            string expectedMd5 = !string.IsNullOrEmpty(srcRef.Md5)
                ? srcRef.Md5
                : resourceEntry?.Md5 ?? "";
            ulong expectedSize = srcRef.Size > 0
                ? srcRef.Size
                : resourceEntry?.Size ?? 0;

            string localPath = Path.Combine(installPath, dest.Replace('/', Path.DirectorySeparatorChar));
            long fileSizeForProgress = File.Exists(localPath)
                ? new FileInfo(localPath).Length
                : expectedSize > 0 ? (long)expectedSize : 0;
            onFileStarted?.Invoke(dest, fileSizeForProgress, checkedCount);

            if (await LocalFileMatchesAsync(
                    installPath, dest, expectedSize, expectedMd5, ReportHashBytes, token)
                    .ConfigureAwait(false))
            {
                alreadyMatchedCount++;
                reportFileCompleted?.Invoke();
                continue;
            }

            // Partial-update resume: file may already be at the patch target version.
            // The apply step skips krpdiff for such groups, so do not downgrade via CDN.
            if (destinationRefs.TryGetValue(dest, out WuwaApiResponsePatchFileRef? dstRef)
                && dstRef is { Md5: { Length: > 0 } md5 }
                && await LocalFileMatchesAsync(
                    installPath, dest, dstRef.Size, md5, hashBytesCallback: null, token)
                    .ConfigureAwait(false))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[WuwaSourceReconciliation] {Dest} already at patch target version; skipping source repair.",
                    dest);
                alreadyMatchedCount++;
                reportFileCompleted?.Invoke();
                continue;
            }

            if (string.IsNullOrEmpty(expectedMd5) && expectedSize == 0)
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaSourceReconciliation] No CDN hash/size for source file {Dest}; skipping repair.",
                    dest);
                skippedCount++;
                reportFileCompleted?.Invoke();
                continue;
            }

            if (resourceEntry == null)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaSourceReconciliation] No resource-index entry for source file {Dest}; " +
                    "cannot CDN-repair the installed-version copy.",
                    dest);
                skippedCount++;
                reportFileCompleted?.Invoke();
                continue;
            }

            if (!string.IsNullOrEmpty(resourceEntry.Md5) && !string.IsNullOrEmpty(expectedMd5)
                && !string.Equals(resourceEntry.Md5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaSourceReconciliation] Resource-index MD5 for {Dest} does not match patch source hash " +
                    "(index={IndexMd5}, source={SourceMd5}); skipping CDN repair.",
                    dest, resourceEntry.Md5, expectedMd5);
                skippedCount++;
                reportFileCompleted?.Invoke();
                continue;
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaSourceReconciliation] Repairing source file from CDN: {Dest}",
                dest);

            if (expectedSize > 0)
                addToTotalBytes?.Invoke((long)expectedSize);

            await DownloadCanonicalFileAsync(
                installer,
                cdnHost,
                baseUrl,
                dest,
                resourceEntry,
                expectedSize,
                expectedMd5,
                installPath,
                reportBytes,
                ReportHashBytes,
                token).ConfigureAwait(false);

            repairedCount++;
            reportFileCompleted?.Invoke();
        }

        if (hashBytesAccum > 0)
            reportProgress?.Invoke();

        return new ReconcileResult(checkedCount, repairedCount, skippedCount, alreadyMatchedCount);
    }

    private static async Task<bool> LocalFileMatchesAsync(
        string installPath,
        string dest,
        ulong expectedSize,
        string expectedMd5,
        Action<long>? hashBytesCallback,
        CancellationToken token)
    {
        string localPath = Path.Combine(installPath, dest.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localPath))
            return false;

        var fi = new FileInfo(localPath);
        if (expectedSize > 0 && (ulong)fi.Length != expectedSize)
        {
            hashBytesCallback?.Invoke(fi.Length);
            return false;
        }

        if (string.IsNullOrEmpty(expectedMd5))
        {
            if (expectedSize > 0)
                hashBytesCallback?.Invoke(fi.Length);
            return expectedSize > 0;
        }

        await using var fs = File.OpenRead(localPath);
        string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, hashBytesCallback, token).ConfigureAwait(false);
        return string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadCanonicalFileAsync(
        WuwaGameInstaller installer,
        string cdnHost,
        string defaultBaseUrl,
        string dest,
        WuwaApiResponseResourceEntry? resourceEntry,
        ulong expectedSize,
        string expectedMd5,
        string installPath,
        Action<long>? downloadBytesCallback,
        Action<long>? hashBytesCallback,
        CancellationToken token)
    {
        string encodedDest = WuwaGameInstaller.EncodePathSegments(dest);
        string fileUrl = BuildFileDownloadUrl(cdnHost, defaultBaseUrl, encodedDest, resourceEntry);
        Uri uri = new(fileUrl, UriKind.Absolute);

        string relativePath = dest.Replace('/', Path.DirectorySeparatorChar);
        string outputPath = Path.Combine(installPath, relativePath);
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        WuwaApiResponseResourceChunkInfo[]? chunkInfos = resourceEntry?.ChunkInfos;
        if (chunkInfos is { Length: > 0 })
        {
            await installer.TryDownloadChunkedFileWithFallbacksAsync(
                uri, outputPath, chunkInfos, dest, token, downloadBytesCallback).ConfigureAwait(false);
        }
        else
        {
            await installer.TryDownloadWholeFileWithFallbacksAsync(
                uri, outputPath, dest, token, downloadBytesCallback).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(expectedMd5))
        {
            await using var stream = File.OpenRead(outputPath);
            string md5 = await WuwaUtils.ComputeMd5HexAsync(stream, hashBytesCallback, token).ConfigureAwait(false);
            if (!string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Repaired source file MD5 mismatch for {dest}: expected={expectedMd5}, computed={md5}");
            }
        }
        else if (expectedSize > 0)
        {
            var fi = new FileInfo(outputPath);
            if ((ulong)fi.Length != expectedSize)
            {
                throw new InvalidOperationException(
                    $"Repaired source file size mismatch for {dest}: expected={expectedSize}, actual={fi.Length}");
            }
        }

        SharedStatic.InstanceLogger.LogDebug(
            "[WuwaSourceReconciliation] Repaired source file: {Dest}", dest);
    }

    private static string BuildFileDownloadUrl(
        string cdnHost,
        string defaultBaseUrl,
        string encodedDest,
        WuwaApiResponseResourceEntry? resourceEntry)
    {
        if (!string.IsNullOrEmpty(resourceEntry?.FromFolder))
        {
            string folder = resourceEntry.FromFolder.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(cdnHost))
                return $"{cdnHost}/{folder.TrimStart('/')}/{encodedDest}";

            return $"{folder}/{encodedDest}";
        }

        return $"{defaultBaseUrl}/{encodedDest}";
    }
}
