using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
#if !USELIGHTWEIGHTJSONPARSER
using System.Text.Json.Nodes;
using Hi3Helper.Plugin.Core.Utility.Json;
#endif

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypos

namespace Hi3Helper.Plugin.Wuwa.Management;

[GeneratedComClass]
internal partial class WuwaGameManager : GameManagerBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= WuwaUtils.CreateApiHttpClient(ApiResponseBaseUrl, GameTag, AuthenticationHash, ApiOptions, Hash1);
        set;
    }
    
    [field: AllowNull, MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= WuwaUtils.CreateApiHttpClient(ApiResponseBaseUrl, GameTag, AuthenticationHash, ApiOptions, Hash1);
        set;
    }
    
    protected override string ApiResponseBaseUrl { get; }
    protected string GameTag { get; set; }
    protected string AuthenticationHash { get; set; }
    protected string ApiOptions { get; set; }
    protected string Hash1 { get; set; }
    
    private WuwaApiResponseGameConfig? ApiGameConfigResponse { get; set; }
    // private WuwaApiResponse<WuwaApiResponseGameConfigRef>? ApiGameDownloadRefResponse { get; set; }
    private string CurrentGameExecutableByPreset { get; }

    private JsonObject CurrentGameConfigNode { get; set; } = new();
    
    internal string? GameResourceJsonUrl   { get; set; }
    internal string? GameResourceBaseUrl   { get; set; }
    internal string? GameResourceBasisPath { get; set; }
    internal bool    IsInitialized         { get; set; }

    internal WuwaGameManager(string gameExecutableNameByPreset,
        string apiResponseBaseUrl, string authenticationHash, string gameTag, string hash1)
    {
        CurrentGameExecutableByPreset = gameExecutableNameByPreset;
        AuthenticationHash = authenticationHash;
        ApiResponseBaseUrl = apiResponseBaseUrl;
        GameTag = gameTag;
        Hash1 = hash1;
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
            // ApiGameDownloadRefResponse = null;
            base.Dispose();
        }
    }
    
    protected override GameVersion CurrentGameVersion
    {
#if !USELIGHTWEIGHTJSONPARSER
        get
        {
            string? version = CurrentGameConfigNode.GetConfigValue<string?>("version");
            if (version == null)
            {
                return GameVersion.Empty;
            }

            if (!GameVersion.TryParse(version, null, out GameVersion currentGameVersion))
            {
                currentGameVersion = GameVersion.Empty;
            }

            return currentGameVersion;
        }
        set => CurrentGameConfigNode.SetConfigValue("version", value.ToString());
#else
        get => CurrentGameConfig.Version;
        set => CurrentGameConfig.Version = value;
#endif
    }
    
    protected override GameVersion ApiGameVersion
    {
        get
        {
            if (ApiGameConfigResponse == null)
            {
                return GameVersion.Empty;
            }

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
            string executablePath1 = Path.Combine(CurrentGameInstallPath ?? string.Empty, CurrentGameExecutableByPreset);
            string executablePath2 = Path.Combine(CurrentGameInstallPath ?? string.Empty, Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset), "Client-Win64-ShippingBase.dll");
            string executablePath3 = Path.Combine(CurrentGameInstallPath ?? string.Empty, "WutheringWaves.exe");
            string executablePath4 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset), "ThirdParty/KrPcSdk_Global/KRSDKRes"), "KRSDK.bin");
            return File.Exists(executablePath1) && File.Exists(executablePath2) && File.Exists(executablePath3) && File.Exists(executablePath4);
        }
    }
    
    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion) => CurrentGameVersion = gameVersion;

    protected override void SetGamePathInner(string gamePath) => CurrentGameInstallPath = gamePath;
    protected override Task<int> InitAsync(CancellationToken token) => InitAsyncInner(true, token);

    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default)
    {
        if (!forceInit && IsInitialized)
            return 0;
        
        string gameConfigUrl = ApiResponseBaseUrl + "/launcher/game/G153/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/index.json";
        
        using HttpResponseMessage configMessage = await ApiResponseHttpClient.GetAsync(gameConfigUrl, HttpCompletionOption.ResponseHeadersRead, token);
        configMessage.EnsureSuccessStatusCode();
#if USELIGHTWEIGHTJSONPARSER
        await using Stream configStream = await configMessage.Content.ReadAsStreamAsync(token);
        ApiGameConfigResponse = await WuwaApiResponseGameConfig.ParseFromAsync(configStream, token: token);
#else
        string jsonResponse = await configMessage.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("API Game Config response: {JsonResponse}", jsonResponse);
        var tmp = JsonSerializer.Deserialize<WuwaApiResponseGameConfig>(jsonResponse, WuwaApiResponseContext.Default.WuwaApiResponseGameConfig);
        ApiGameConfigResponse = tmp ?? throw new JsonException("Failed to deserialize API game config response.");

#endif
        
        if (ApiGameConfigResponse.Default?.ConfigReference == null)
            throw new NullReferenceException("ApiGameConfigResponse.ResponseData is null");
        
        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
        {
            throw new NullReferenceException("Game API Launcher cannot retrieve CurrentVersion value!");
        }

        GameResourceBasisPath = ApiGameConfigResponse.Default.ConfigReference.BaseUrl;
        if (GameResourceBasisPath == null)
        {
            throw new NullReferenceException("Game API Launcher cannot retrieve BaseUrl reference value!");
        }
        
        Uri gameResourceBase = new Uri(ApiResponseBaseUrl); // TODO for Cry0: Replace this as the actual base URL for the game resources from default.cdnList.url
        GameResourceBaseUrl = $"{gameResourceBase.Scheme}://{gameResourceBase.Host}";

        // Set API current game version
        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
        {
            throw new InvalidOperationException($"API GameConfig returns an invalid CurrentVersion data! Data: {ApiGameConfigResponse.Default.ConfigReference.CurrentVersion}");
        }

        ApiGameVersion = ApiGameConfigResponse.Default.ConfigReference.CurrentVersion;
        IsInitialized  = true;

        return 0;
    }

    protected override Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress, CancellationToken token)
    {
        return base.DownloadAssetAsyncInner(client, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    internal HttpClient GetDownloadClient() => ApiDownloadHttpClient; // TODO: Use this to pass the HttpClient to the IGameInstaller instance.

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
        => Task.Factory.StartNew<string?>(() =>
        {
            
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return null;
            string? rootSearchPath = Path.GetDirectoryName(Path.GetDirectoryName(CurrentGameInstallPath));
            if (string.IsNullOrEmpty(rootSearchPath))
                return null;

            // ReSharper disable once LoopCanBeConvertedToQuery
            string? gameName = Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset);
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("Start finding game existing installation using prefix: {PrefixName} from root path: {RootPath}", gameName, rootSearchPath);
#endif
            foreach (string dirPath in Directory.EnumerateDirectories(rootSearchPath, $"{gameName}", SearchOption.AllDirectories))
            {
#if DEBUG
                SharedStatic.InstanceLogger.LogTrace("Checking for game presence in directory: {DirPath}", dirPath);
#endif
                foreach (string path in Directory.EnumerateFiles(dirPath, $"*{gameName}*", SearchOption.TopDirectoryOnly))
                {
#if DEBUG
                    SharedStatic.InstanceLogger.LogTrace("Got executable file at: {ExecPath}", path);
#endif
                    string? parentPath = Path.GetDirectoryName(path);
                    if (parentPath == null)
                        continue;

                    string jsonPath = Path.Combine(parentPath, "game-launcher-config.json");
                    if (File.Exists(jsonPath))
                    {
                        return parentPath;
                    }
                }
            }

            return null;
        }, token);

    
    public override void LoadConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning("[HBRGameManager::LoadConfig] Game directory isn't set! Game config won't be loaded.");
            return;
        }

        string   filePath = Path.Combine(CurrentGameInstallPath, "game-launcher-config.json");
        FileInfo fileInfo = new FileInfo(filePath);

        if (!fileInfo.Exists)
        {
            SharedStatic.InstanceLogger.LogWarning("[HBRGameManager::LoadConfig] File game-launcher-config.json doesn't exist on dir: {Dir}", CurrentGameInstallPath);
            return;
        }

        try
        {
            using FileStream fileStream = fileInfo.OpenRead();
#if USELIGHTWEIGHTJSONPARSER
            CurrentGameConfig = HBRGameLauncherConfig.ParseFrom(fileStream);
#else
            CurrentGameConfigNode = JsonNode.Parse(fileStream) as JsonObject ?? new JsonObject();
#endif
            SharedStatic.InstanceLogger.LogTrace("[HBRGameManager::LoadConfig] Loaded game-launcher-config.json from directory: {Dir}", CurrentGameInstallPath);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[HBRGameManager::LoadConfig] Cannot load game-launcher-config.json! Reason: {Exception}", ex);
        }
    }

    public override void SaveConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning("[HBRGameManager::LoadConfig] Game directory isn't set! Game config won't be saved.");
            return;
        }

#if !USELIGHTWEIGHTJSONPARSER
        CurrentGameConfigNode.SetConfigValueIfEmpty("version", CurrentGameVersion.ToString());
        CurrentGameConfigNode.SetConfigValueIfEmpty("name", ApiGameConfigResponse?.KeyFileCheckList?[2] ?? Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset));
#endif
        if (CurrentGameVersion == GameVersion.Empty)
        {
            SharedStatic.InstanceLogger.LogWarning("[HBRGameManager::SaveConfig] Current version returns 0.0.0! Overwrite the version to current provided version by API, {VersionApi}", ApiGameVersion);
            CurrentGameVersion = ApiGameVersion;
        }
    }
}