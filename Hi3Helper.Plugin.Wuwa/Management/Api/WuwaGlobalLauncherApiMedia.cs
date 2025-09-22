using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Utils;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

[GeneratedComClass]
internal partial class WuwaGlobalLauncherApiMedia(string apiResponseBaseUrl, string gameTag, string authenticationHash, string apiOptions, string hash1) : LauncherApiMediaBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient { 
        get => field ??= WuwaUtils.CreateApiHttpClient(ApiResponseBaseUrl, gameTag, authenticationHash, apiOptions, hash1);
        set;
    }

    [field: AllowNull, MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl { get; } = apiResponseBaseUrl;
    private WuwaApiResponseMedia? ApiResponse { get; set; }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        using (ThisInstanceLock.EnterScope())
        {
            PluginDisposableMemory<LauncherPathEntry> backgroundEntries = PluginDisposableMemory<LauncherPathEntry>.Alloc();

            try
            {
                ref LauncherPathEntry entry = ref backgroundEntries[0];

                if (ApiResponse == null)
                {
                    isDisposable = false;
                    handle = nint.Zero;
                    count = 0;
                    isAllocated = false;
                    return;
                }

                entry.Write(ApiResponse.BackgroundImageUrl, Span<byte>.Empty);
                isAllocated = true;
            }
            finally
            {
                isDisposable = backgroundEntries.IsDisposable == 1;
                handle = backgroundEntries.AsSafePointer();
                count = backgroundEntries.Length;
            }
        }
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
        => result = LauncherBackgroundFlag.TypeIsVideo | LauncherBackgroundFlag.TypeIsImage;

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
        => result = LauncherBackgroundFlag.None;

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        isDisposable = false;
        handle = nint.Zero;
        count = 0;
        isAllocated = false;
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await ApiResponseHttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, ApiResponseHttpClient.BaseAddress), token);
        response.EnsureSuccessStatusCode();

        string jsonResponse = await response.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("API Media response: {JsonResponse}", jsonResponse);
        ApiResponse = JsonSerializer.Deserialize<WuwaApiResponseMedia>(jsonResponse, WuwaApiResponseContext.Default.WuwaApiResponseMedia)
                      ?? throw new NullReferenceException("Background Media API Returns null response!");

        // We don't have a way to check if the API response is valid, so we assume it is valid if we reach this point.
        return 0;
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress, CancellationToken token)
    {
        await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;
        
        using (ThisInstanceLock.EnterScope())
        {
            ApiResponseHttpClient.Dispose();
            ApiDownloadHttpClient.Dispose();
            
            ApiResponse = null;
            base.Dispose();
        }
    }
}

