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
