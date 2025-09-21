using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Utility;
using System.Net;

// ReSharper disable InconsistentNaming

#if USELIGHTWEIGHTJSONPARSER
using System.IO;
#else
using System.Text.Json;
#endif

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

[GeneratedComClass]
internal partial class WuwaGlobalLauncherApiNews(string apiResponseBaseUrl, string gameTag, string authenticationHash, string apiOptions, string hash1) : LauncherApiNewsBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= WuwaUtils.CreateApiHttpClient(apiResponseBaseUrl, gameTag, authenticationHash, apiOptions, hash1);
        set;
    }

    [field: AllowNull, MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.GZip)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl { get; } = apiResponseBaseUrl;
    private WuwaApiResponseSocial? SocialMediaApiResponse { get; set; }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        string requestUrl = ApiResponseBaseUrl
            .CombineUrlFromString("launcher",
                gameTag,
                authenticationHash.AeonPlsHelpMe(),
                "social",
                "en.json");

        using HttpResponseMessage response = await ApiResponseHttpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
#if USELIGHTWEIGHTJSONPARSER
        await using Stream networkStream = await response.Content.ReadAsStreamAsync(token);
        SocialMediaResponse = await WuwaApiResponseSocial.ParseFromAsync(networkStream, token: token);
#else
        string jsonResponse = await response.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("API Social Media and News response: {JsonResponse}", jsonResponse);
        SocialMediaApiResponse = JsonSerializer.Deserialize<WuwaApiResponseSocial>(jsonResponse, WuwaApiResponseContext.Default.WuwaApiResponseSocial)
            ?? throw new NullReferenceException("News and Social Media API Returns null response!");
#endif
        
        return !response.IsSuccessStatusCode ? (int)response.StatusCode : 0;
    }
    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
        => InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);


    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
        => InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        try
        {
            if (SocialMediaApiResponse?.SocialMediaEntries is null
                || SocialMediaApiResponse.SocialMediaEntries.Count == 0)
            {
                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGlobalLauncherApiNews::GetSocialMediaEntries] API provided no social media entries, returning empty handle.");
                InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
                return;
            }

            List<WuwaApiResponseSocialResponse> validEntries =
            [
                ..SocialMediaApiResponse.SocialMediaEntries
                    .Where(x => !string.IsNullOrEmpty(x.SocialMediaName) &&
                                !string.IsNullOrEmpty(x.ClickUrl) &&
                                !string.IsNullOrEmpty(x.IconUrl)
                    )
            ];
            int entryCount = validEntries.Count;
            PluginDisposableMemory<LauncherSocialMediaEntry> memory =
                PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(entryCount);

            handle = memory.AsSafePointer();
            count = entryCount;
            isDisposable = true;

            SharedStatic.InstanceLogger.LogTrace(
                "[WuwaGlobalLauncherApiNews::GetSocialMediaEntries] {EntryCount} entries are allocated at: 0x{Address:x8}",
                entryCount, handle);

            for (int i = 0; i < entryCount; i++)
            {
                string socialMediaName = validEntries[i].SocialMediaName!;
                string clickUrl = validEntries[i].ClickUrl!;
                string? iconUrl = validEntries[i].IconUrl;

                ref LauncherSocialMediaEntry unmanagedEntries = ref memory[i];

                unmanagedEntries.WriteIcon(iconUrl);
                unmanagedEntries.WriteDescription(socialMediaName);
                unmanagedEntries.WriteClickUrl(clickUrl);
            }

            isAllocated = true;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("Failed to get social media entries: {ErrorMessage}", ex.Message);
            SharedStatic.InstanceLogger.LogDebug(ex, "Exception details: {ExceptionDetails}", ex);
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
        }
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress, CancellationToken token)
    {
        await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    private void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;

        using (ThisInstanceLock.EnterScope())
        {
            ApiDownloadHttpClient.Dispose();
            ApiResponseHttpClient = null!;

            SocialMediaApiResponse = null;
            base.Dispose();
        }
    }
    
}