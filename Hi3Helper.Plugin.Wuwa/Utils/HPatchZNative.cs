using System;
using System.IO;
using System.Threading;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using SharpHPatchZ;
using SharpHPatchZ.Header;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Utils;

/// <summary>
/// Wrapper around SharpHPatchZ for applying KRPDiff patches
/// (HDiff19 + ZSTD + Fadler64).
/// </summary>
internal static class HPatchZNative
{
    /// <summary>
    /// Returns the expected combined size of old (source) reference data from the krpdiff header.
    /// Returns -1 if the header cannot be parsed.
    /// </summary>
    internal static long GetExpectedOldSize(string diffFilePath)
    {
        try
        {
            HDiffInfo info = HPatch.CreateInstance(diffFilePath, new InitializeOptions { IsKuroGamesHDiff = true });
            try
            {
                return HPatch.TryGetPatchMetadata(ref info, out var metadata) ? metadata.DiffOldSize : -1;
            }
            finally
            {
                info.Dispose();
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Detects errors indicating on-disk source bytes do not match what the krpdiff
    /// was built from (wrong size/version/corruption). The installer uses this to fall back to downloading full files.
    /// </summary>
    internal static bool IsLikelySourceDataMismatch(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (Exception inner in agg.InnerExceptions)
            {
                if (IsLikelySourceDataMismatch(inner))
                    return true;
            }

            return false;
        }

        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is ArgumentOutOfRangeException or IndexOutOfRangeException)
                return true;

            if (cur.Message.Contains("out of bounds", StringComparison.OrdinalIgnoreCase))
                return true;

            if (cur is InvalidOperationException &&
                (cur.Message.Contains("input file size does not match", StringComparison.OrdinalIgnoreCase) ||
                 cur.Message.Contains("input file size mismatched", StringComparison.OrdinalIgnoreCase)))
                return true;

            // PatchDir throws InvalidDataException when combined source stream size
            // doesn't match the krpdiff's expected oldDataSize. This is deterministic
            // and retrying buffer modes cannot fix it.
            if (cur is InvalidDataException &&
                (cur.Message.Contains("unmatch size", StringComparison.OrdinalIgnoreCase) ||
                 cur.Message.Contains("source file size mismatch", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Apply a KRPDiff patch file to a source file, producing a new output file.
    /// Uses SharpHPatchZ (managed C# HDiff implementation).
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
            token.ThrowIfCancellationRequested();
            using HDiffInfo info = HPatch.CreateInstance(diffFilePath);
            PatchResult result = HPatch.Patch(info, diffFilePath, sourceFilePath, outputFilePath,
                options: PatchOptions.Default, token: token);
            if (!result)
                throw result.Exception ?? new InvalidOperationException("Patch failed without an exception.");
        }
        catch (OperationCanceledException)
        {
            // Clean up partial output on cancellation
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); }
            catch { /* ignore cleanup errors */ }
            throw;
        }
        catch (Exception ex) when (FindCancellation(ex) is { } oce)
        {
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); }
            catch { /* ignore cleanup errors */ }
            throw oce;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[HPatchZNative::ApplyPatch] Patch failed for {Source}: {Error}",
                sourceFilePath, FormatExceptionChain(ex));

            // Clean up partial output on failure
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); }
            catch { /* ignore cleanup errors */ }

            throw new InvalidOperationException(
                $"HDiff patch application failed for source: {sourceFilePath}, diff: {diffFilePath}", ex);
        }

        SharedStatic.InstanceLogger.LogDebug(
            "[HPatchZNative::ApplyPatch] Patch applied successfully: {Output}", outputFilePath);
    }

    /// <summary>
    /// Apply a KRPDiff directory-level patch: the diff was built from a set of source files
    /// under <paramref name="sourceDir"/> and produces a set of output files under
    /// <paramref name="outputDir"/>. SharpHPatchZ auto-detects directory mode from
    /// the diff header and resolves internal file references relative to the supplied paths.
    /// </summary>
    /// <param name="sourceDir">Root directory containing the original (old) files.</param>
    /// <param name="diffFilePath">Path to the .krpdiff file (directory-level diff).</param>
    /// <param name="outputDir">Directory where patched (new) files will be written.</param>
    /// <param name="writeBytesDelegate">Optional callback invoked with the number of bytes
    /// written during patching, for progress reporting.</param>
    /// <param name="token">Cancellation token.</param>
    /// <exception cref="DirectoryNotFoundException">Thrown if source directory does not exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown if diff file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if patching fails.</exception>
    internal static void ApplyDirPatch(string sourceDir, string diffFilePath, string outputDir,
        Action<long>? writeBytesDelegate = null, CancellationToken token = default)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory for patching not found: {sourceDir}");
        if (!File.Exists(diffFilePath))
            throw new FileNotFoundException("Diff file for patching not found.", diffFilePath);

        Directory.CreateDirectory(outputDir);

        SharedStatic.InstanceLogger.LogDebug(
            "[HPatchZNative::ApplyDirPatch] Applying dir patch: srcDir={Source}, diff={Diff}, outDir={Output}",
            sourceDir, diffFilePath, outputDir);

        try
        {
            token.ThrowIfCancellationRequested();
            using HDiffInfo info = HPatch.CreateInstance(diffFilePath,
                new InitializeOptions { IsKuroGamesHDiff = true });
            PatchResult result = HPatch.Patch(info, diffFilePath, sourceDir, outputDir, options: PatchOptions.Default,
                progressCallback: writeBytesDelegate == null ? null : (_, _, written) => writeBytesDelegate(written),
                token: token);
            if (!result)
                throw result.Exception ?? new InvalidOperationException("Directory patch failed without an exception.");
        }
        catch (OperationCanceledException)
        {
            try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); }
            catch { /* ignore cleanup errors */ }
            throw;
        }
        catch (Exception ex) when (FindCancellation(ex) is { } oce)
        {
            try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); }
            catch { /* ignore cleanup errors */ }
            throw oce;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[HPatchZNative::ApplyDirPatch] Dir patch failed for {Source}: {Error}",
                sourceDir, FormatExceptionChain(ex));

            try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); }
            catch { /* ignore cleanup errors */ }

            throw new InvalidOperationException(
                $"HDiff dir patch application failed for sourceDir: {sourceDir}, diff: {diffFilePath}", ex);
        }

        SharedStatic.InstanceLogger.LogDebug(
            "[HPatchZNative::ApplyDirPatch] Dir patch applied successfully: {Output}", outputDir);
    }

    /// <summary>
    /// Walks the exception's InnerException chain (and AggregateException.InnerExceptions)
    /// looking for an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static OperationCanceledException? FindCancellation(Exception ex)
    {
        if (ex is OperationCanceledException oce)
            return oce;

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                var found = FindCancellation(inner);
                if (found != null)
                    return found;
            }
        }

        return ex.InnerException != null ? FindCancellation(ex.InnerException) : null;
    }

    private static string FormatExceptionChain(Exception ex)
    {
        if (ex is AggregateException agg)
            return string.Join(" | ", agg.Flatten().InnerExceptions);

        return ex.InnerException != null
            ? $"{ex.Message} -> {FormatExceptionChain(ex.InnerException)}"
            : ex.Message;
    }
}
