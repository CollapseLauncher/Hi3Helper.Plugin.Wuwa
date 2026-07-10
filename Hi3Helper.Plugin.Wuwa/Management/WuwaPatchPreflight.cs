using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

/// <summary>
/// Shared pre-flight validation for patch flows — checks whether installed files
/// already match the target version described in a patch index.
/// </summary>
internal static class WuwaPatchPreflight
{
    internal readonly record struct Result(int CheckedCount, HashSet<string> MismatchedDstFiles);

    internal readonly record struct BadSourceFile(string Dest, string Reason);

    internal static bool AllFilesMatch(in Result result) =>
        result.CheckedCount > 0 && result.MismatchedDstFiles.Count == 0;

    /// <summary>
    /// Returns true when every destination file in the group exists on disk with the
    /// expected size and MD5 (files without MD5 in the manifest are ignored).
    /// </summary>
    internal static async Task<bool> GroupDestinationsMatchAsync(
        string installPath,
        WuwaApiResponsePatchGroupInfo group,
        CancellationToken token)
    {
        bool checkedAny = false;

        foreach (var dstRef in group.DstFiles)
        {
            if (string.IsNullOrEmpty(dstRef.Dest) || string.IsNullOrEmpty(dstRef.Md5))
                continue;

            checkedAny = true;
            token.ThrowIfCancellationRequested();

            string dstPath = Path.Combine(installPath,
                dstRef.Dest.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(dstPath))
                return false;

            var fi = new FileInfo(dstPath);
            if (dstRef.Size > 0 && (ulong)fi.Length != dstRef.Size)
                return false;

            await using var fs = File.OpenRead(dstPath);
            string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token).ConfigureAwait(false);
            if (!string.Equals(md5, dstRef.Md5, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return checkedAny;
    }

    /// <summary>
    /// Returns source files that are missing, have an unexpected size, or (when listed in
    /// the manifest) an MD5 that does not match — any of which can make krpdiff output wrong.
    /// </summary>
    internal static async Task<List<BadSourceFile>> FindBadSourceFilesAsync(
        string installPath,
        WuwaApiResponsePatchGroupInfo group,
        CancellationToken token)
    {
        var badFiles = new List<BadSourceFile>();

        foreach (var srcRef in group.SrcFiles)
        {
            if (string.IsNullOrEmpty(srcRef.Dest))
                continue;

            token.ThrowIfCancellationRequested();

            string srcPath = Path.Combine(installPath,
                srcRef.Dest.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(srcPath))
            {
                badFiles.Add(new BadSourceFile(srcRef.Dest, "missing"));
                continue;
            }

            var fi = new FileInfo(srcPath);
            if (srcRef.Size > 0 && (ulong)fi.Length != srcRef.Size)
            {
                badFiles.Add(new BadSourceFile(srcRef.Dest,
                    $"size mismatch (expected={srcRef.Size}, actual={fi.Length})"));
                continue;
            }

            if (!string.IsNullOrEmpty(srcRef.Md5))
            {
                await using var fs = File.OpenRead(srcPath);
                string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token).ConfigureAwait(false);
                if (!string.Equals(md5, srcRef.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    // Partial-update resume: file may already be at the patch target version.
                    // Krpdiffs require the 3.4.1 source bytes — a 3.5.0 file will crash HPatch
                    // or produce garbage even when the manifest source hash "should" apply.
                    WuwaApiResponsePatchFileRef? pairedDst = FindPairedDestination(group, srcRef.Dest);
                    if (pairedDst is { Md5: { Length: > 0 } dstMd5 }
                        && (ulong)fi.Length == pairedDst.Size
                        && string.Equals(md5, dstMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        badFiles.Add(new BadSourceFile(srcRef.Dest,
                            "already at patch target version (invalid krpdiff source)"));
                    }
                    else
                    {
                        badFiles.Add(new BadSourceFile(srcRef.Dest,
                            $"MD5 mismatch (expected={srcRef.Md5}, actual={md5})"));
                    }

                    continue;
                }
            }

            // Size-only manifest entry: reject files that already match the paired destination.
            WuwaApiResponsePatchFileRef? dstOnlyCheck = FindPairedDestination(group, srcRef.Dest);
            if (dstOnlyCheck is { Md5: { Length: > 0 } dstMd5Only, Size: > 0 }
                && (ulong)fi.Length == dstOnlyCheck.Size)
            {
                await using var dstCheckStream = File.OpenRead(srcPath);
                string dstCheckMd5 = await WuwaUtils.ComputeMd5HexAsync(dstCheckStream, token)
                    .ConfigureAwait(false);
                if (string.Equals(dstCheckMd5, dstMd5Only, StringComparison.OrdinalIgnoreCase))
                {
                    badFiles.Add(new BadSourceFile(srcRef.Dest,
                        "already at patch target version (invalid krpdiff source)"));
                }
            }
        }

        return badFiles;
    }

    private static WuwaApiResponsePatchFileRef? FindPairedDestination(
        WuwaApiResponsePatchGroupInfo group,
        string srcDest)
    {
        int pairCount = Math.Min(group.SrcFiles.Length, group.DstFiles.Length);
        for (int i = 0; i < pairCount; i++)
        {
            if (string.Equals(group.SrcFiles[i].Dest, srcDest, StringComparison.OrdinalIgnoreCase))
                return group.DstFiles[i];
        }

        return null;
    }

    /// <summary>
    /// Returns null when the file matches the expected size and MD5; otherwise a reason string.
    /// </summary>
    internal static async Task<string?> ValidateLocalFileAsync(
        string filePath,
        ulong expectedSize,
        string expectedMd5,
        CancellationToken token)
    {
        if (!File.Exists(filePath))
            return "missing";

        var fi = new FileInfo(filePath);
        if (expectedSize > 0 && (ulong)fi.Length != expectedSize)
            return $"size mismatch (expected={expectedSize}, actual={fi.Length})";

        if (string.IsNullOrEmpty(expectedMd5))
            return expectedSize > 0 ? null : "no hash or size to verify";

        await using var fs = File.OpenRead(filePath);
        string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token: token).ConfigureAwait(false);
        return string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"MD5 mismatch (expected={expectedMd5}, actual={md5})";
    }

    internal static async Task<Result> VerifyInstalledFilesAsync(
        string installPath,
        WuwaApiResponsePatchIndex patchIndex,
        CancellationToken token)
    {
        var mismatchedDstFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int checkedCount = 0;

        if (patchIndex.GroupInfos.Length == 0)
            return new Result(0, mismatchedDstFiles);

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
                    mismatchedDstFiles.Add(dstRef.Dest);
                    checkedCount++;
                    continue;
                }

                var fi = new FileInfo(dstPath);
                if (dstRef.Size > 0 && (ulong)fi.Length != dstRef.Size)
                {
                    mismatchedDstFiles.Add(dstRef.Dest);
                    checkedCount++;
                    continue;
                }

                await using var fs = File.OpenRead(dstPath);
                string md5 = await WuwaUtils.ComputeMd5HexAsync(fs, token).ConfigureAwait(false);
                if (!string.Equals(md5, dstRef.Md5, StringComparison.OrdinalIgnoreCase))
                    mismatchedDstFiles.Add(dstRef.Dest);

                checkedCount++;
            }
        }

        return new Result(checkedCount, mismatchedDstFiles);
    }
}
