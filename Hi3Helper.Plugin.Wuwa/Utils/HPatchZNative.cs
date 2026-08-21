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
    /// Returns the expected combined size of old (source) reference data from the krpdiff header.
    /// Returns -1 if the header cannot be parsed.
    /// </summary>
    internal static long GetExpectedOldSize(string diffFilePath)
    {
        try
        {
            return HDiffPatch.GetHDiffOldSize(diffFilePath);
        }
        catch
        {
            return -1;
        }
    }

    private readonly struct DirPatchMode(bool buffered, bool fullBuffer, bool fastBuffer)
    {
        internal bool Buffered { get; } = buffered;
        internal bool FullBuffer { get; } = fullBuffer;
        internal bool FastBuffer { get; } = fastBuffer;
    }

    private static readonly DirPatchMode[] DirPatchModes =
    [
        // Library default recommendation (README): buffered + fastBuffer, no fullBuffer.
        new(buffered: true,  fullBuffer: false, fastBuffer: true),
        new(buffered: true,  fullBuffer: false, fastBuffer: false),
        new(buffered: false, fullBuffer: false, fastBuffer: false),
        // Last resort for small diffs only — fullBuffer can blow up on multi-GB paks.
        new(buffered: true,  fullBuffer: true,  fastBuffer: false),
    ];

    /// <summary>
    /// SharpHDiffPatch throws this when on-disk source bytes do not match what the krpdiff
    /// was built from (wrong size/version/corruption). Retrying buffer modes cannot fix it.
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
    /// <paramref name="outputDir"/>. SharpHDiffPatch.Core auto-detects directory mode from
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

        Exception? lastEx = null;
        foreach (DirPatchMode mode in DirPatchModes)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(outputDir))
                {
                    try { Directory.Delete(outputDir, true); }
                    catch { /* best-effort */ }
                }

                Directory.CreateDirectory(outputDir);

                var patcher = new HDiffPatch();
                patcher.Initialize(diffFilePath);
                patcher.DirPatchFormat = DirectoryPatchFormat.Kuro;
                RunDirPatch(patcher, sourceDir, outputDir, mode, writeBytesDelegate, token);

                SharedStatic.InstanceLogger.LogDebug(
                    "[HPatchZNative::ApplyDirPatch] Dir patch applied successfully with buffered={Buffered}, " +
                    "fullBuffer={FullBuffer}, fastBuffer={FastBuffer}: {Output}",
                    mode.Buffered, mode.FullBuffer, mode.FastBuffer, outputDir);
                return;
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
                lastEx = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[HPatchZNative::ApplyDirPatch] Attempt failed (buffered={Buffered}, fullBuffer={FullBuffer}, " +
                    "fastBuffer={FastBuffer}) for {Source}: {Error}",
                    mode.Buffered, mode.FullBuffer, mode.FastBuffer, sourceDir, FormatExceptionChain(ex));

                try
                {
                    if (Directory.Exists(outputDir))
                        Directory.Delete(outputDir, true);
                }
                catch { /* ignore cleanup errors */ }

                if (IsLikelySourceDataMismatch(ex))
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[HPatchZNative::ApplyDirPatch] Source bytes do not match krpdiff metadata for {Source}; " +
                        "skipping remaining buffer modes.",
                        sourceDir);
                    break;
                }
            }
        }

        SharedStatic.InstanceLogger.LogError(
            "[HPatchZNative::ApplyDirPatch] Dir patch failed for {Source} after all retry modes",
            sourceDir);

        throw new InvalidOperationException(
            $"HDiff dir patch application failed for sourceDir: {sourceDir}, diff: {diffFilePath}", lastEx);
    }

    private static void RunDirPatch(
        HDiffPatch patcher,
        string sourceDir,
        string outputDir,
        DirPatchMode mode,
        Action<long>? writeBytesDelegate,
        CancellationToken token)
    {
        if (writeBytesDelegate != null)
        {
            patcher.Patch(sourceDir, outputDir, useBufferedPatch: mode.Buffered, writeBytesDelegate,
                token: token, useFullBuffer: mode.FullBuffer, useFastBuffer: mode.FastBuffer);
            return;
        }

        patcher.Patch(sourceDir, outputDir, useBufferedPatch: mode.Buffered, token: token,
            useFullBuffer: mode.FullBuffer, useFastBuffer: mode.FastBuffer);
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
