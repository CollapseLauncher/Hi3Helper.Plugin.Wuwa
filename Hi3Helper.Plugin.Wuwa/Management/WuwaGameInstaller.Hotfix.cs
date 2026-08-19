using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal partial class WuwaGameInstaller
{
    private const string HotfixTempDirName = "TempHotfixFiles";

    private bool TryGetKnownHotfix(out WuwaKnownHotfixPatch patch, out string gamePath)
    {
        WuwaKnownHotfixPatch? available =
            (GameManager as WuwaGameManager)?.AvailableHotfixPatch;
        GameManager.GetGamePath(out string? path);
        GameManager.GetCurrentGameVersion(out GameVersion currentVersion);
        GameManager.GetApiGameVersion(out GameVersion apiVersion);
        gamePath = path ?? "";
        patch = available!;
        return gamePath.Length > 0 &&
               available != null &&
               currentVersion == apiVersion &&
               currentVersion.ToString() == patch.PackageVersion &&
               patch.CanApply(gamePath);
    }

    private async Task<long> GetKnownHotfixDownloadedSizeAsync(CancellationToken token)
    {
        if (!TryGetKnownHotfix(out WuwaKnownHotfixPatch patch, out string gamePath))
            return 0;

        string downloadRoot = GetHotfixDownloadRoot(gamePath, patch);
        long total = 0;
        foreach (WuwaHotfixPatchFile file in patch.Downloads)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(downloadRoot, file.Name);
            if (await ValidateChecksumAsync(path, file, token).ConfigureAwait(false))
                total += file.Size;
        }

        return total;
    }

    private Task StartKnownHotfixAsync(
        WuwaKnownHotfixPatch patch,
        string gamePath,
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token) =>
        RunKnownHotfixAsync(patch, gamePath, progressDelegate, progressStateDelegate, token);

    private async Task RunKnownHotfixAsync(
        WuwaKnownHotfixPatch patch,
        string gamePath,
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Close Wuthering Waves before applying its hotfix.");

        string packageRoot = patch.GetPackageRoot(gamePath);
        string workRoot = Path.Combine(gamePath, "TempPath", HotfixTempDirName, patch.WorkName);
        string downloadRoot = GetHotfixDownloadRoot(gamePath, patch);
        string stageRoot = Path.Combine(workRoot, "Stage");
        Dictionary<string, string> stagePaths = patch.Components.ToDictionary(
            x => x.ResourceType,
            x => Path.Combine(stageRoot, x.ResourceType),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> finalPaths = patch.Components.ToDictionary(
            x => x.ResourceType,
            x => patch.GetResourceVersionPath(gamePath, x.ResourceType, x.TargetVersion),
            StringComparer.OrdinalIgnoreCase);

        WuwaHotfixComponent? existingTarget = patch.Components.FirstOrDefault(
            x => Directory.Exists(finalPaths[x.ResourceType]));
        if (existingTarget != null)
        {
            throw new InvalidOperationException(
                $"The {existingTarget.ResourceType} {existingTarget.TargetVersion} hotfix target " +
                "already exists but is not mounted. " +
                "Run the game's repair once before applying it externally.");
        }

        if (Directory.Exists(stageRoot))
            Directory.Delete(stageRoot, true);
        Directory.CreateDirectory(downloadRoot);
        foreach (string stagePath in stagePaths.Values)
            Directory.CreateDirectory(stagePath);

        WuwaHotfixPatchFile[] downloads = patch.Downloads.ToArray();
        var progress = new InstallProgress
        {
            TotalBytesToDownload = patch.DownloadSize,
            TotalCountToDownload = downloads.Length,
            TotalStateToComplete = patch.Files.Count() + patch.Components.Count,
        };

        void ReportProgress()
        {
            InstallProgress snapshot = progress;
            progressDelegate?.Invoke(in snapshot);
        }

        try
        {
            progressStateDelegate?.Invoke(InstallProgressState.Download);
            foreach (WuwaHotfixPatchFile file in downloads)
            {
                token.ThrowIfCancellationRequested();
                string destination = Path.Combine(downloadRoot, file.Name);
                if (!await ValidateChecksumAsync(destination, file, token).ConfigureAwait(false))
                {
                    await DownloadHotfixFileAsync(
                        patch.GetDownloadUri(file),
                        destination,
                        file,
                        bytes =>
                        {
                            progress.DownloadedBytes += bytes;
                            ReportProgress();
                        },
                        token).ConfigureAwait(false);
                }
                else
                {
                    progress.DownloadedBytes += file.Size;
                }

                progress.DownloadedCount++;
                ReportProgress();
            }

            progressStateDelegate?.Invoke(InstallProgressState.Verify);
            foreach (WuwaHotfixPatchFile file in downloads)
            {
                string path = Path.Combine(downloadRoot, file.Name);
                if (!await ValidateChecksumAsync(path, file, token).ConfigureAwait(false))
                    throw new InvalidDataException($"Hotfix file verification failed: {file.Name}");
            }

            progressStateDelegate?.Invoke(InstallProgressState.Updating);
            foreach (WuwaHotfixPatchFile file in patch.Files)
            {
                token.ThrowIfCancellationRequested();
                WuwaHotfixComponent component = patch.Components.Single(x =>
                    x.ResourceType.Equals(file.ResourceType, StringComparison.OrdinalIgnoreCase));
                string source = patch.GetResourceVersionPath(
                    gamePath, file.ResourceType, component.SourceVersion);
                string aggregate = stagePaths[file.ResourceType];
                string groupOutput = Path.Combine(stageRoot, "Group", file.Name);
                string diffPath = Path.Combine(downloadRoot, file.Name);

                await Task.Run(
                    () => HPatchZNative.ApplyDirPatch(source, diffPath, groupOutput, token: token),
                    token).ConfigureAwait(false);
                MergePatchOutput(groupOutput, aggregate);
                progress.StateCount++;
                ReportProgress();
            }

            var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (WuwaHotfixComponent component in patch.Components)
            {
                mounts[component.ResourceType] = await BuildMountManifestAsync(
                    stagePaths[component.ResourceType],
                    $"{component.ResourceType}/{component.TargetVersion}",
                    component.TargetVersion,
                    token).ConfigureAwait(false);
                progress.StateCount++;
                ReportProgress();
                Directory.CreateDirectory(Path.GetDirectoryName(finalPaths[component.ResourceType])!);
            }

            string manifestRoot = Path.Combine(packageRoot, "ResManifest");
            Directory.CreateDirectory(manifestRoot);
            foreach (WuwaHotfixPatchFile manifest in patch.Manifests)
            {
                if (File.Exists(Path.Combine(manifestRoot, manifest.Name)))
                {
                    throw new InvalidOperationException(
                        $"The hotfix manifest already exists: {manifest.Name}. " +
                        "Run the game's repair once before applying it externally.");
                }
            }

            string mountRoot = Path.Combine(packageRoot, "Mount");
            Directory.CreateDirectory(mountRoot);
            Dictionary<string, string> mountPaths = patch.Components.ToDictionary(
                x => x.ResourceType,
                x => Path.Combine(mountRoot, $"Mount{x.ResourceType}.txt"),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string?> previousMounts = mountPaths.ToDictionary(
                x => x.Key,
                x => File.Exists(x.Value) ? File.ReadAllText(x.Value) : null,
                StringComparer.OrdinalIgnoreCase);
            var movedComponents = new List<WuwaHotfixComponent>();
            var copiedManifests = new List<string>();
            try
            {
                foreach (WuwaHotfixComponent component in patch.Components)
                {
                    Directory.Move(
                        stagePaths[component.ResourceType], finalPaths[component.ResourceType]);
                    movedComponents.Add(component);
                }

                foreach (WuwaHotfixPatchFile manifest in patch.Manifests)
                {
                    string destination = Path.Combine(manifestRoot, manifest.Name);
                    AtomicCopy(Path.Combine(downloadRoot, manifest.Name), destination);
                    copiedManifests.Add(destination);
                }

                foreach (WuwaHotfixComponent component in patch.Components)
                {
                    AtomicWrite(
                        mountPaths[component.ResourceType], mounts[component.ResourceType]);
                }
            }
            catch
            {
                try
                {
                    foreach (WuwaHotfixComponent component in patch.Components)
                    {
                        RestoreText(
                            mountPaths[component.ResourceType],
                            previousMounts[component.ResourceType]);
                    }

                    foreach (string manifestPath in copiedManifests)
                    {
                        if (File.Exists(manifestPath))
                            File.Delete(manifestPath);
                    }

                    foreach (WuwaHotfixComponent component in movedComponents.AsEnumerable().Reverse())
                    {
                        string finalPath = finalPaths[component.ResourceType];
                        if (Directory.Exists(finalPath))
                            Directory.Move(finalPath, stagePaths[component.ResourceType]);
                    }
                }
                catch (Exception rollbackError)
                {
                    SharedStatic.InstanceLogger.LogError(
                        "[WuwaGameInstaller::Hotfix] Commit rollback failed: {Error}",
                        rollbackError.Message);
                }

                throw;
            }

            WuwaHotfixComponent? launcher = patch.Components.FirstOrDefault(
                x => x.ResourceType == "Launcher");
            WuwaHotfixComponent? resource = patch.Components.FirstOrDefault(
                x => x.ResourceType == "Resource");
            WuwaHotfixVersionStorage.TryUpdate(
                gamePath,
                launcher?.TargetVersion,
                resource?.TargetVersion,
                launcher == null ? null : mounts["Launcher"]);
            foreach (WuwaHotfixComponent component in patch.Components)
            {
                TryDeleteOldHotfixSource(patch.GetResourceVersionPath(
                    gamePath, component.ResourceType, component.SourceVersion));
            }

            progress.StateCount = progress.TotalStateToComplete;
            ReportProgress();
            progressStateDelegate?.Invoke(InstallProgressState.Completed);

            try { Directory.Delete(workRoot, true); }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameInstaller::Hotfix] Could not remove temporary files: {Error}", ex.Message);
            }
            string transitions = string.Join(", ", patch.Components.Select(
                x => $"{x.ResourceType} {x.SourceVersion} -> {x.TargetVersion}"));
            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameInstaller::Hotfix] Applied Windows hotfix: {Transitions}",
                transitions);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stageRoot))
                    Directory.Delete(stageRoot, true);
            }
            catch
            {
                // Preserve the original patch error.
            }

            throw;
        }
    }

    private async Task DownloadHotfixFileAsync(
        Uri uri,
        string destination,
        WuwaHotfixPatchFile expected,
        Action<long> progress,
        CancellationToken token)
    {
        string tempPath = destination + ".download";
        using HttpResponseMessage response = await _downloadHttpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                progress(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
        output.Close();
        if (!await ValidateChecksumAsync(tempPath, expected, token).ConfigureAwait(false))
            throw new InvalidDataException($"Downloaded hotfix file failed verification: {expected.Name}");
        File.Move(tempPath, destination, true);
    }

    private static async Task<bool> ValidateChecksumAsync(
        string path, WuwaHotfixPatchFile expected, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected.Size)
            return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = expected.Md5 != null
            ? await MD5.HashDataAsync(stream, token).ConfigureAwait(false)
            : await SHA1.HashDataAsync(stream, token).ConfigureAwait(false);
        string checksum = expected.Md5 ?? expected.Sha1;
        return Convert.ToHexString(hash).Equals(checksum, StringComparison.OrdinalIgnoreCase);
    }

    private static void MergePatchOutput(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            if (File.Exists(target))
                throw new InvalidDataException($"Hotfix groups produced the same output file: {relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target);
        }

        Directory.Delete(source, true);
    }

    private static async Task<string> BuildMountManifestAsync(
        string root, string relativeRoot, string targetVersion, CancellationToken token)
    {
        if (!int.TryParse(targetVersion.AsSpan(targetVersion.LastIndexOf('.') + 1), out int patchNumber))
            throw new InvalidDataException($"Invalid hotfix version: {targetVersion}");

        int mountOrder = patchNumber + 4;
        var builder = new StringBuilder("::Mount::\n");
        int pakCount = 0;
        foreach (string pak in Directory.EnumerateFiles(root, "*.pak", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            pakCount++;
            token.ThrowIfCancellationRequested();
            string basePath = Path.Combine(
                Path.GetDirectoryName(pak)!, Path.GetFileNameWithoutExtension(pak));
            string relative = Path.GetRelativePath(root, basePath).Replace('\\', '/');
            string pakHash = await GetOptionalSha1Async(basePath + ".pak", token).ConfigureAwait(false);
            string sigHash = await GetOptionalSha1Async(basePath + ".sig", token).ConfigureAwait(false);
            string utocHash = await GetOptionalSha1Async(basePath + ".utoc", token).ConfigureAwait(false);
            string ucasHash = await GetOptionalSha1Async(basePath + ".ucas", token).ConfigureAwait(false);
            builder.Append(relativeRoot).Append('/').Append(relative).Append(',')
                .Append(mountOrder).Append(',').Append(pakHash).Append(',').Append(sigHash)
                .Append(',').Append(utocHash).Append(',').Append(ucasHash).Append('\n');
        }

        if (pakCount == 0)
            throw new InvalidDataException($"Hotfix produced no PAK files in {root}.");

        return builder.Append("::Del::\n").ToString();
    }

    private static async Task<string> GetOptionalSha1Async(string path, CancellationToken token)
    {
        if (!File.Exists(path))
            return "";

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static void AtomicCopy(string source, string destination)
    {
        string tempPath = destination + ".collapse.tmp";
        File.Copy(source, tempPath, true);
        File.Move(tempPath, destination, true);
    }

    private static void AtomicWrite(string destination, string content)
    {
        string tempPath = destination + ".collapse.tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
        File.Move(tempPath, destination, true);
    }

    private static void RestoreText(string path, string? content)
    {
        if (content == null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        AtomicWrite(path, content);
    }

    private static void TryDeleteOldHotfixSource(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameInstaller::Hotfix] Could not remove old overlay {Path}: {Error}",
                path, ex.Message);
        }
    }

    private static string GetHotfixDownloadRoot(string gamePath, WuwaKnownHotfixPatch patch) =>
        Path.Combine(gamePath, "TempPath", HotfixTempDirName, patch.WorkName, "Download");

    private static bool IsGameRunning()
    {
        Process[] processes = Process.GetProcessesByName("Client-Win64-Shipping");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }
}
