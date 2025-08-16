using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Update;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable AccessToDisposedClosure

namespace Indexer;

public class SelfUpdateAssetInfo
{
    public required string FilePath { get; set; }

    public long Size { get; set; }

    public required byte[] FileHash { get; set; }
}

public class Program
{
    private static readonly string[]             AllowedPluginExt             = [".dll", ".exe", ".so", ".dylib"];
    private static readonly SearchValues<string> AllowedPluginExtSearchValues = SearchValues.Create(AllowedPluginExt, StringComparison.OrdinalIgnoreCase);
    private static readonly string               PackageExtension             = ".zip";

    public static int Main(params string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return int.MaxValue;
        }

        try
        {
            string path = args[0];
            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine("Path is not a directory or it doesn't exist!");
                return 2;
            }

            FileInfo? fileInfo = FindPluginLibraryAndGetAssets(path, out List<SelfUpdateAssetInfo> assetInfo, out PluginManifest? reference);
            if (fileInfo == null || reference == null || string.IsNullOrEmpty(reference.MainLibraryName))
            {
                Console.Error.WriteLine("No valid plugin library was found.");
                return 1;
            }

            string referenceFilePath = Path.Combine(path, "manifest.json");
            int retCode = WriteToJson(reference, referenceFilePath, assetInfo);
            return retCode != 0 ? retCode : PackFiles(path, reference, assetInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An unknown error has occurred! {ex}");
            return int.MinValue;
        }
    }

    private static int WriteToJson(PluginManifest reference, string referenceFilePath, List<SelfUpdateAssetInfo> assetInfo)
    {
        DateTimeOffset creationDate = reference.PluginCreationDate.ToOffset(reference.PluginCreationDate.Offset);

        Console.WriteLine("Plugin has been found!");
        Console.WriteLine($"  Main Library Path Name: {reference.MainLibraryName}");
        Console.WriteLine($"  Main Plugin Name: {reference.MainPluginName}");
        Console.WriteLine($"  Creation Date: {creationDate}");
        Console.WriteLine($"  Version: {reference.PluginVersion}");
        Console.Write("Writing metadata info...");

        using FileStream referenceFileStream = File.Create(referenceFilePath);
        using Utf8JsonWriter writer = new Utf8JsonWriter(referenceFileStream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n"
        });

        writer.WriteStartObject();

        writer.WriteString(nameof(PluginManifest.MainLibraryName), reference.MainLibraryName);
        writer.WriteString(nameof(PluginManifest.MainPluginName), reference.MainPluginName);
        writer.WriteString(nameof(PluginManifest.MainPluginAuthor), reference.MainPluginAuthor);
        writer.WriteString(nameof(PluginManifest.MainPluginDescription), reference.MainPluginDescription);
        writer.WriteString(nameof(PluginManifest.PluginStandardVersion), reference.PluginStandardVersion.ToString());
        writer.WriteString(nameof(PluginManifest.PluginVersion), reference.PluginVersion.ToString());
        writer.WriteString(nameof(PluginManifest.PluginCreationDate), creationDate);
        writer.WriteString(nameof(PluginManifest.ManifestDate), reference.ManifestDate);
        if (reference.PluginAlternativeIcon?.Length != 0)
        {
            writer.WriteString(nameof(PluginManifest.PluginAlternativeIcon), reference.PluginAlternativeIcon);
        }

        writer.WriteStartArray("Assets");
        foreach (var asset in assetInfo)
        {
            writer.WriteStartObject();

            writer.WriteString(nameof(asset.FilePath), asset.FilePath);
            writer.WriteNumber(nameof(asset.Size), asset.Size);
            writer.WriteBase64String(nameof(asset.FileHash), asset.FileHash);

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();

        Console.WriteLine(" Done!");
        return 0;
    }

    private static int PackFiles(string outputPath, PluginManifest referenceInfo, List<SelfUpdateAssetInfo> fileList)
    {
        string packageName = $"{Path.GetFileNameWithoutExtension(referenceInfo.MainLibraryName)}_{referenceInfo.PluginVersion}_API-{referenceInfo.PluginStandardVersion}_{referenceInfo.ManifestDate.ToString("yyyyMMdd")}";
        string packageFilePath = Path.Combine(outputPath, packageName + PackageExtension);

        int threads = Environment.ProcessorCount;

        Console.WriteLine($"Writing output package in parallel using {threads} threads at: {packageFilePath}...");

        try
        {
            using FileStream packageFileStream = File.Create(packageFilePath);
            using ZipArchive packageWriter = new ZipArchive(packageFileStream, ZipArchiveMode.Create, false, Encoding.UTF8);

            fileList.Add(new SelfUpdateAssetInfo
            {
                FileHash = [],
                FilePath = "manifest.json"
            });

            int length = fileList.Count;
            int count = 0;

            Lock thisLock = new Lock();
            Parallel.ForEach(fileList, CompressBrotliAndCreate);

            return 0;

            void CompressBrotliAndCreate(SelfUpdateAssetInfo asset)
            {
                Interlocked.Increment(ref count);
                int currentCount = count;

                string filePath = Path.Combine(outputPath, asset.FilePath);
                DateTime lastWriteTime = File.GetLastWriteTimeUtc(filePath);

                if (lastWriteTime.Year is < 1980 or > 2107)
                    lastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0);

                Console.WriteLine($"  [{currentCount}/{length}] Compressing asset to buffer: {asset.FilePath}");
                using MemoryStream compressedStream = new MemoryStream(); 
                using BrotliStream brotliStream = new BrotliStream(compressedStream, CompressionLevel.SmallestSize);
                using FileStream fileStream = File.OpenRead(filePath);

                fileStream.CopyTo(brotliStream);
                brotliStream.Flush();

                compressedStream.Position = 0;

                using (thisLock.EnterScope())
                {
                    Console.WriteLine($"  [{currentCount}/{length}] Compress done. Now locking and writing buffer to package for: {asset.FilePath}");

                    string entryBrExt = asset.FilePath + ".br";
                    ZipArchiveEntry entry = packageWriter.CreateEntry(entryBrExt, CompressionLevel.NoCompression);
                    entry.LastWriteTime = lastWriteTime;

                    using Stream entryStream = entry.Open();
                    compressedStream.CopyTo(entryStream);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return Marshal.GetHRForException(e);
        }
    }

    private static FileInfo? FindPluginLibraryAndGetAssets(string dirPath, out List<SelfUpdateAssetInfo> fileList, out PluginManifest? referenceInfo)
    {
        DirectoryInfo directoryInfo = new DirectoryInfo(dirPath);
        List<SelfUpdateAssetInfo> fileListRef = [];
        fileList = fileListRef;
        referenceInfo = null;

        FileInfo? mainLibraryFileInfo = null;
        PluginManifest? referenceInfoResult = null;

        Parallel.ForEach(directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories).Where(x => !x.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)), Impl);
        referenceInfo = referenceInfoResult;

        return mainLibraryFileInfo;

        void Impl(FileInfo fileInfo)
        {
            string fileName = fileInfo.FullName.AsSpan(directoryInfo.FullName.Length).TrimStart("\\/").ToString();
            if (fileName.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (mainLibraryFileInfo == null &&
                IsPluginLibrary(fileInfo, fileName, out PluginManifest? referenceInfoInner))
            {
                Interlocked.Exchange(ref mainLibraryFileInfo, fileInfo);
                Interlocked.Exchange(ref referenceInfoResult, referenceInfoInner);
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(4 << 10);
            try
            {
                MD5 hash = MD5.Create();
                using FileStream fileStream = fileInfo.OpenRead();

                int read;
                while ((read = fileStream.Read(buffer)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                }

                hash.TransformFinalBlock(buffer, 0, read);

                byte[] hashBytes = hash.Hash ?? [];
                SelfUpdateAssetInfo assetInfo = new SelfUpdateAssetInfo
                {
                    FileHash = hashBytes,
                    FilePath = fileName,
                    Size = fileInfo.Length
                };

                lock (fileListRef)
                {
                    fileListRef.Add(assetInfo);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private unsafe delegate void* GetPlugin();
    private unsafe delegate GameVersion* GetVersion();

    private static unsafe bool IsPluginLibrary(FileInfo fileInfo, string fileName, [NotNullWhen(true)] out PluginManifest? referenceInfo)
    {
        nint handle = nint.Zero;
        referenceInfo = null;

        if (fileInfo.Name.IndexOfAny(AllowedPluginExtSearchValues) < 0)
        {
            return false;
        }
        char* getPluginNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPlugin");
        char* getPluginVersionNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPluginVersion");
        char* getGetPluginStandardVersionNameP = (char*)Utf16StringMarshaller.ConvertToUnmanaged("GetPluginStandardVersion");

        try
        {
            if (!NativeLibrary.TryLoad(fileInfo.FullName, out handle) ||
                !NativeLibrary.TryGetExport(handle, "TryGetApiExport", out nint exportAddress) ||
                exportAddress == nint.Zero)
            {
                return false;
            }

            delegate* unmanaged[Cdecl]<char*, void**, int> tryGetApiExportCallback = (delegate* unmanaged[Cdecl]<char*, void**, int>)exportAddress;

            nint getPluginP = nint.Zero;
            int tryResult = tryGetApiExportCallback(getPluginNameP, (void**)&getPluginP);

            if (tryResult != 0 ||
                getPluginP == nint.Zero)
            {
                return false;
            }

            void* pluginP = Marshal.GetDelegateForFunctionPointer<GetPlugin>(getPluginP)();
            if (pluginP == null)
            {
                return false;
            }

            IPlugin? plugin = ComInterfaceMarshaller<IPlugin>.ConvertToManaged(pluginP);
            if (plugin == null)
            {
                return false;
            }

            tryResult = tryGetApiExportCallback(getPluginVersionNameP, (void**)&getPluginP);

            if (tryResult != 0 ||
                getPluginP == nint.Zero)
            {
                return false;
            }

            GameVersion pluginVersion = *Marshal.GetDelegateForFunctionPointer<GetVersion>(getPluginP)();

            tryResult = tryGetApiExportCallback(getGetPluginStandardVersionNameP, (void**)&getPluginP);

            if (tryResult != 0 ||
                getPluginP == nint.Zero)
            {
                return false;
            }

            GameVersion pluginStandardVersion = *Marshal.GetDelegateForFunctionPointer<GetVersion>(getPluginP)();

            plugin.GetPluginName(out string? pluginName);
            plugin.GetPluginAuthor(out string? pluginAuthor);
            plugin.GetPluginDescription(out string? pluginDescription);
            plugin.GetPluginCreationDate(out DateTime* pluginCreationDate);

            referenceInfo = new PluginManifest
            {
                Assets = [],
                MainPluginName = pluginName,
                MainPluginAuthor = pluginAuthor,
                MainPluginDescription = pluginDescription,
                PluginCreationDate = *pluginCreationDate,
                PluginVersion = pluginVersion,
                PluginStandardVersion = pluginStandardVersion,
                PluginAlternativeIcon = TryGetAlternateIconData(plugin),
                MainLibraryName = fileName,
                ManifestDate = DateTimeOffset.UtcNow
            };
            return true;
        }
        finally
        {
            if (handle != nint.Zero)
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    private static void PrintHelp()
    {
        string? execPath = Path.GetFileName(Environment.ProcessPath);
        Console.WriteLine($"Usage: {execPath} [plugin_dll_directory_path]");
    }

    private static string? TryGetAlternateIconData(IPlugin plugin)
    {
        try
        {
            plugin.GetPluginAppIconUrl(out string? iconUrlOrData);
            if (string.IsNullOrEmpty(iconUrlOrData))
            {
                return null;
            }

            if (Base64.IsValid(iconUrlOrData))
            {
                return iconUrlOrData;
            }

            if (!Uri.TryCreate(iconUrlOrData, UriKind.Absolute, out Uri? iconUrl))
            {
                return null;
            }

            return iconUrl.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error while retrieving plugin alternative icon data: {ex}");
            return null;
        }
    }
}