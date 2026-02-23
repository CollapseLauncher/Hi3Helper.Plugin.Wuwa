using System;
using System.IO;
using System.Threading;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using SharpHDiffPatch.Core;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Utils;

/// <summary>
/// Wrapper around SharpHDiffPatch.Core for applying KRPDiff patches
/// (HDiff19 + ZSTD + Fadler64).
/// </summary>
internal static class HPatchZNative
{
    /// <summary>
    /// Apply a KRPDiff patch file to a source file, producing a new output file.
    /// Uses SharpHDiffPatch.Core (managed C# HDiff implementation).
    /// </summary>
    /// <param name="sourceFilePath">Path to the original file to be patched.</param>
    /// <param name="diffFilePath">Path to the .krpdiff file.</param>
    /// <param name="outputFilePath">Path where the patched output should be written.</param>
    /// <param name="token">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">Thrown if source or diff file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if patching fails.</exception>
    internal static void ApplyPatch(string sourceFilePath, string diffFilePath, string outputFilePath,
        CancellationToken token = default)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Source file for patching not found.", sourceFilePath);
        if (!File.Exists(diffFilePath))
            throw new FileNotFoundException("Diff file for patching not found.", diffFilePath);

        // Ensure the output directory exists
        string? outputDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        SharedStatic.InstanceLogger.LogDebug(
            "[HPatchZNative::ApplyPatch] Applying patch: src={Source}, diff={Diff}, out={Output}",
            sourceFilePath, diffFilePath, outputFilePath);

        try
        {
            var patcher = new HDiffPatch();
            patcher.Initialize(diffFilePath);
            patcher.Patch(sourceFilePath, outputFilePath, useBufferedPatch: true, token: token,
                useFullBuffer: false, useFastBuffer: true);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial output on cancellation
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); }
            catch { /* ignore cleanup errors */ }
            throw;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[HPatchZNative::ApplyPatch] Patch failed for {Source}: {Error}",
                sourceFilePath, ex.Message);

            // Clean up partial output on failure
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); }
            catch { /* ignore cleanup errors */ }

            throw new InvalidOperationException(
                $"HDiff patch application failed for source: {sourceFilePath}, diff: {diffFilePath}", ex);
        }

        SharedStatic.InstanceLogger.LogDebug(
            "[HPatchZNative::ApplyPatch] Patch applied successfully: {Output}", outputFilePath);
    }
}
