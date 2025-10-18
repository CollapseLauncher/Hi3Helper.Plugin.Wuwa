using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Core.Utility.Json;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypos

namespace Hi3Helper.Plugin.Wuwa.Management;

[GeneratedComClass]
internal partial class WuwaGameManager : GameManagerBase
{
    internal WuwaGameManager(string gameExecutableNameByPreset,
        string apiResponseAssetUrl,
        string authenticationHash,
        string gameTag,
        string clientAccess,
        string currentPatch,
        string hash1,
        string hash2)
    {
        CurrentGameExecutableByPreset = gameExecutableNameByPreset;
        AuthenticationHash = authenticationHash;
        ApiResponseAssetUrl = apiResponseAssetUrl;
        GameTag = gameTag;
        ClientAccess = clientAccess;
        CurrentPatch = currentPatch;
        Hash1 = hash1;
        Hash2 = hash2;
    }

    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??=
            WuwaUtils.CreateApiHttpClient(ApiResponseAssetUrl, GameTag, AuthenticationHash, "", Hash1);
        set;
    }

    [field: AllowNull, MaybeNull]
    private HttpClient ApiDownloadHttpClient
    {
        get => field ??=
            WuwaUtils.CreateApiHttpClient(ApiResponseAssetUrl, GameTag, AuthenticationHash, "", Hash1);
        set;
    }

    private string ApiResponseAssetUrl { get; }
    private string GameTag { get; set; }
    private string ClientAccess { get; set; }
    private string CurrentPatch { get; set; }
    private string AuthenticationHash { get; set; }
    private string Hash1 { get; set; }
    private string Hash2 { get; set; }

    private WuwaApiResponseGameConfig? ApiGameConfigResponse { get; set; }
    private string CurrentGameExecutableByPreset { get; }

    private JsonObject CurrentGameConfigNode { get; set; } = new();

    internal string? GameResourceBaseUrl { get; set; }
    private string? GameResourceBasisPath { get; set; }
    private bool IsInitialized { get; set; }
    

    protected override GameVersion CurrentGameVersion
    {
        get
        {
            string? version = CurrentGameConfigNode.GetConfigValue<string?>("version");
            if (version == null) return GameVersion.Empty;

            if (!GameVersion.TryParse(version, null, out GameVersion currentGameVersion))
                currentGameVersion = GameVersion.Empty;

            return currentGameVersion;
        }
        set => CurrentGameConfigNode.SetConfigValue("version", value.ToString());
    }

    protected override GameVersion ApiGameVersion
    {
        get
        {
            if (ApiGameConfigResponse == null) return GameVersion.Empty;

            field = ApiGameConfigResponse.Default?.ConfigReference?.CurrentVersion ?? GameVersion.Empty;
            return field;
        }
        set;
    }

    protected override bool HasPreload => ApiPreloadGameVersion != GameVersion.Empty && !HasUpdate;
    protected override bool HasUpdate => IsInstalled && ApiGameVersion != CurrentGameVersion;

    protected override bool IsInstalled
    {
        get
        {
            string executablePath1 =
                Path.Combine(CurrentGameInstallPath ?? string.Empty, CurrentGameExecutableByPreset);
            string executablePath2 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset), "Client-Win64-ShippingBase.dll");
            string executablePath3 = Path.Combine(CurrentGameInstallPath ?? string.Empty, "WutheringWaves.exe");
            string executablePath4 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset),
                    "ThirdParty/KrPcSdk_Global/KRSDKRes"), "KRSDK.bin");
            return File.Exists(executablePath1) && File.Exists(executablePath2) && File.Exists(executablePath3) &&
                   File.Exists(executablePath4);
        }
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;

        using (ThisInstanceLock.EnterScope())
        {
            ApiDownloadHttpClient.Dispose();
            ApiDownloadHttpClient = null!;
            ApiGameConfigResponse = null;

            base.Dispose();
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        CurrentGameVersion = gameVersion;
    }

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return InitAsyncInner(true, token);
    }

    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default)
    {
        if (!forceInit && IsInitialized)
            return 0;

        string gameConfigUrl =
            $"https://prod-alicdn-gamestarter.kurogame.com/launcher/game/{GameTag.AeonPlsHelpMe()}/" +
            $"{AuthenticationHash.AeonPlsHelpMe()}/index.json";

        using HttpResponseMessage configMessage =
            await ApiResponseHttpClient.GetAsync(gameConfigUrl, HttpCompletionOption.ResponseHeadersRead, token);
        configMessage.EnsureSuccessStatusCode();

        string jsonResponse = await configMessage.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("API Game Config response: {JsonResponse}", jsonResponse);
        WuwaApiResponseGameConfig? tmp = JsonSerializer.Deserialize<WuwaApiResponseGameConfig>(jsonResponse,
            WuwaApiResponseContext.Default.WuwaApiResponseGameConfig);
        ApiGameConfigResponse = tmp ?? throw new JsonException("Failed to deserialize API game config response.");

        if (ApiGameConfigResponse.Default?.ConfigReference == null)
            throw new NullReferenceException("ApiGameConfigResponse.ResponseData is null");

        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
            throw new NullReferenceException("Game API Launcher cannot retrieve CurrentVersion value!");

        GameResourceBasisPath = ApiGameConfigResponse.Default.ConfigReference.BaseUrl;
        if (GameResourceBasisPath == null)
            throw new NullReferenceException("Game API Launcher cannot retrieve BaseUrl reference value!");

        Uri gameResourceBase =
            new(ApiResponseAssetUrl);
        GameResourceBaseUrl = $"https://{ApiResponseAssetUrl.AeonPlsHelpMe()}/launcher/game/" +
                              $"{GameTag.AeonPlsHelpMe()}/{ClientAccess.AeonPlsHelpMe()}/{CurrentPatch}/" +
                              $"{Hash2.AeonPlsHelpMe()}/resource/{ClientAccess.AeonPlsHelpMe()}/{CurrentPatch}/indexFile.json";
        
        SharedStatic.InstanceLogger.LogDebug("Game Resource Base URL: {GameResourceBaseUrl}", GameResourceBaseUrl);

        // Set API current game version
        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
            throw new InvalidOperationException(
                $"API GameConfig returns an invalid CurrentVersion data! Data: {ApiGameConfigResponse.Default.ConfigReference.CurrentVersion}");

        ApiGameVersion = ApiGameConfigResponse.Default.ConfigReference.CurrentVersion;
        IsInitialized = true;

        return 0;
    }

    protected override Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        return base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return null;

            string? rootSearchPath = Path.GetDirectoryName(Path.GetDirectoryName(CurrentGameInstallPath));
            if (string.IsNullOrEmpty(rootSearchPath))
                return null;

            string gameName = Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset);

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple
            };

#if DEBUG
        SharedStatic.InstanceLogger.LogTrace("Start finding game existing installation using prefix: {PrefixName} from root path: {RootPath}", gameName, rootSearchPath);
#endif

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(rootSearchPath, $"*{gameName}*", options))
                {
                    if (token.IsCancellationRequested)
                        return null;

#if DEBUG
                SharedStatic.InstanceLogger.LogTrace("Got executable file at: {ExecPath}", filePath);
#endif

                    string? parentPath = Path.GetDirectoryName(filePath);
                    if (parentPath == null)
                        continue;

                    string jsonPath = Path.Combine(parentPath, "app-game-config.json");
                    if (File.Exists(jsonPath))
                    {
#if DEBUG
                    SharedStatic.InstanceLogger.LogTrace("Found app-game-config.json at: {JsonPath}", jsonPath);
#endif
                        return parentPath;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ArgumentException ex)
            {
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("ArgumentException while enumerating files: {Error}", ex.Message);
#endif
                return null;
            }
            catch (PathTooLongException ex)
            {
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("PathTooLongException while enumerating files: {Error}", ex.Message);
#endif
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("Access denied while enumerating files: {Error}", ex.Message);
#endif
                return null;
            }
            catch (IOException ex)
            {
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("IO error while enumerating files: {Error}", ex.Message);
#endif
                return null;
            }

            return null;
        }, token);


    public override void LoadConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] Game directory isn't set! Game config won't be loaded.");
            return;
        }

        string filePath = Path.Combine(CurrentGameInstallPath, "app-game-config.json");
        FileInfo fileInfo = new(filePath);

        if (!fileInfo.Exists)
        {
            SharedStatic.InstanceLogger.LogWarning(
				"[WuwaGameManager::LoadConfig] File app-game-config.json doesn't exist on dir: {Dir}",
                CurrentGameInstallPath);
            return;
        }

        try
        {
            using FileStream fileStream = fileInfo.OpenRead();
#if USELIGHTWEIGHTJSONPARSER
            CurrentGameConfig = WuwaGameLauncherConfig.ParseFrom(fileStream);
#else
            CurrentGameConfigNode = JsonNode.Parse(fileStream) as JsonObject ?? new JsonObject();
#endif
            SharedStatic.InstanceLogger.LogTrace(
				"[WuwaGameManager::LoadConfig] Loaded app-game-config.json from directory: {Dir}",
                CurrentGameInstallPath);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
				"[WuwaGameManager::LoadConfig] Cannot load app-game-config.json! Reason: {Exception}", ex);
        }
    }

    public override void SaveConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] Game directory isn't set! Game config won't be saved.");
            return;
        }

#if !USELIGHTWEIGHTJSONPARSER
        CurrentGameConfigNode.SetConfigValueIfEmpty("version", CurrentGameVersion.ToString());
        CurrentGameConfigNode.SetConfigValueIfEmpty("name",
            ApiGameConfigResponse?.KeyFileCheckList?[2] ??
            Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset));
#endif
        if (CurrentGameVersion == GameVersion.Empty)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::SaveConfig] Current version returns 0.0.0! Overwrite the version to current provided version by API, {VersionApi}",
                ApiGameVersion);
            CurrentGameVersion = ApiGameVersion;
        }
    }
}