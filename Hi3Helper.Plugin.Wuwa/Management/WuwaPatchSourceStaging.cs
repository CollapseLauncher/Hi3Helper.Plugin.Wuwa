using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.Wuwa.Management;

/// <summary>
/// Builds a minimal source tree for directory-level krpdiffs so HPatch only sees
/// the group's expected source files instead of the full (possibly mixed-version) install.
/// </summary>
internal static class WuwaPatchSourceStaging
{
    /// <summary>
    /// Creates a temp directory containing hard links to the group's source files,
    /// preserving their relative paths. Returns null when staging cannot be created.
    /// </summary>
    internal static string? TryCreate(
        string installPath,
        WuwaApiResponsePatchGroupInfo group,
        string patchTempPath,
        int groupIndex)
    {
        if (group.SrcFiles.Length == 0)
            return null;

        string stagingRoot = Path.Combine(patchTempPath, $"_patch_src_{groupIndex}");
        try
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, true);

            Directory.CreateDirectory(stagingRoot);

            foreach (var srcRef in group.SrcFiles)
            {
                if (string.IsNullOrEmpty(srcRef.Dest))
                    continue;

                string relativePath = srcRef.Dest.Replace('/', Path.DirectorySeparatorChar);
                string sourceFile = Path.Combine(installPath, relativePath);
                if (!File.Exists(sourceFile))
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[WuwaPatchSourceStaging] Cannot stage missing source file: {File}", srcRef.Dest);
                    TryCleanup(stagingRoot);
                    return null;
                }

                string stagedFile = Path.Combine(stagingRoot, relativePath);
                string? stagedDir = Path.GetDirectoryName(stagedFile);
                if (!string.IsNullOrEmpty(stagedDir))
                    Directory.CreateDirectory(stagedDir);

                if (File.Exists(stagedFile))
                    File.Delete(stagedFile);

                if (!TryCreateHardLink(stagedFile, sourceFile))
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[WuwaPatchSourceStaging] Hard link failed for {File}", srcRef.Dest);
                    TryCleanup(stagingRoot);
                    return null;
                }
            }

            return stagingRoot;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaPatchSourceStaging] Failed to create isolated source tree for group {Idx}: {Err}",
                groupIndex, ex.Message);
            TryCleanup(stagingRoot);
            return null;
        }
    }

    internal static void TryCleanup(string? stagingRoot)
    {
        if (string.IsNullOrEmpty(stagingRoot) || !Directory.Exists(stagingRoot))
            return;

        try
        {
            Directory.Delete(stagingRoot, true);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[WuwaPatchSourceStaging] Failed to clean up staging dir {Path}: {Err}",
                stagingRoot, ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkNative(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero);
    }
}
