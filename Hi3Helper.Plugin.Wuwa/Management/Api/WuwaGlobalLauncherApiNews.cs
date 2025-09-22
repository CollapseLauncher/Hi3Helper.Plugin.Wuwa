using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Core.Utility.Json;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

[GeneratedComClass]
internal partial class WuwaGlobalLauncherApiNews(string apiResponseBaseUrl, string gameTag, string authenticationHash, string apiOptions, string hash1) : LauncherApiNewsBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= WuwaUtils.CreateApiHttpClient(ApiResponseBaseUrl, gameTag, authenticationHash, apiOptions, hash1);
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

    protected override string                 ApiResponseBaseUrl         { get; } = apiResponseBaseUrl;
    private            WuwaApiResponseSocial? ApiResponseSocialMedia     { get; set; }
    private            WuwaApiResponseNews?   ApiResponseNewsAndCarousel { get; set; }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        string requestSocialUrl = ApiResponseBaseUrl
               .CombineUrlFromString("launcher",
                        gameTag,
                        authenticationHash.AeonPlsHelpMe(),
                        "social",
                        "en.json");

        string requestNewsUrl = ApiResponseBaseUrl
               .CombineUrlFromString("launcher",
                        authenticationHash.AeonPlsHelpMe(),
                        gameTag,
                        "information",
                        "en.json");

        ApiResponseSocialMedia = await ApiResponseHttpClient
               .GetApiResponseFromJsonAsync(
                        requestSocialUrl,
                        WuwaApiResponseContext.Default.WuwaApiResponseSocial,
                        token);

        ApiResponseNewsAndCarousel = await ApiResponseHttpClient
               .GetApiResponseFromJsonAsync(
                        requestNewsUrl,
                        WuwaApiResponseContext.Default.WuwaApiResponseNews,
                        token);

        return 0;
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (ApiResponseNewsAndCarousel?.NewsData == null)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        int entryEventCount  = ApiResponseNewsAndCarousel.NewsData.ContentKindEvent.Contents.Length;
        int entryNewsCount   = ApiResponseNewsAndCarousel.NewsData.ContentKindNews.Contents.Length;
        int entryNoticeCount = ApiResponseNewsAndCarousel.NewsData.ContentKindNotice.Contents.Length;

        count =  entryEventCount;
        count += entryNewsCount;
        count += entryNoticeCount;

        if (count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherNewsEntry> memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);

        handle       = memory.AsSafePointer();
        isDisposable = true;
        isAllocated  = true;

        int memIndex = 0;
        Write(ApiResponseNewsAndCarousel.NewsData.ContentKindEvent.Contents,  ref memory, ref memIndex);
        Write(ApiResponseNewsAndCarousel.NewsData.ContentKindNews.Contents,   ref memory, ref memIndex);
        Write(ApiResponseNewsAndCarousel.NewsData.ContentKindNotice.Contents, ref memory, ref memIndex);

        return;

        static void Write(Span<WuwaApiResponseNewsEntry> entriesSpan, ref PluginDisposableMemory<LauncherNewsEntry> mem, ref int memOffset)
        {
            for (int i = 0; i < entriesSpan.Length; i++, memOffset++)
            {
                ref LauncherNewsEntry unmanagedEntry = ref mem[memOffset];

                WuwaApiResponseNewsEntry entry = entriesSpan[i];
                unmanagedEntry.Write(entry.NewsTitle, null, entry.ClickUrl, entry.Date);
            }
        }
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (ApiResponseNewsAndCarousel == null)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = ApiResponseNewsAndCarousel.CarouselData.Length;
        if (count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherCarouselEntry> memory = PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);

        handle       = memory.AsSafePointer();
        isDisposable = true;
        isAllocated  = true;

        Span<WuwaApiResponseCarouselEntry> entries = ApiResponseNewsAndCarousel.CarouselData;
        for (int i = 0; i < count; i++)
        {
            ref LauncherCarouselEntry unmanagedEntry = ref memory[i];

            unmanagedEntry.Write(entries[i].Description, entries[i].ImageUrl, entries[i].ClickUrl);
        }
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        try
        {
            if (ApiResponseSocialMedia?.SocialMediaEntries is null
             || ApiResponseSocialMedia.SocialMediaEntries.Count == 0)
            {
                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGlobalLauncherApiNews::GetSocialMediaEntries] API provided no social media entries, returning empty handle.");
                InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
                return;
            }

            List<WuwaApiResponseSocialResponse> validEntries =
            [
                ..ApiResponseSocialMedia.SocialMediaEntries
                                         .Where(x => !string.IsNullOrEmpty(x.SocialMediaName) &&
                                                     !string.IsNullOrEmpty(x.ClickUrl) &&
                                                     !string.IsNullOrEmpty(x.IconUrl)
                                          )
            ];
            int entryCount = validEntries.Count;
            PluginDisposableMemory<LauncherSocialMediaEntry> memory =
                PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(entryCount);

            handle       = memory.AsSafePointer();
            count        = entryCount;
            isDisposable = true;
            isAllocated  = true;

            SharedStatic.InstanceLogger.LogTrace(
                "[WuwaGlobalLauncherApiNews::GetSocialMediaEntries] {EntryCount} entries are allocated at: 0x{Address:x8}",
                entryCount, handle);

            for (int i = 0; i < entryCount; i++)
            {
                string  socialMediaName = validEntries[i].SocialMediaName!;
                string  clickUrl        = validEntries[i].ClickUrl!;
                string? iconUrl         = validEntries[i].IconUrl;

                ref LauncherSocialMediaEntry unmanagedEntries = ref memory[i];

                unmanagedEntries.WriteIcon(iconUrl);
                unmanagedEntries.WriteDescription(socialMediaName);
                unmanagedEntries.WriteClickUrl(clickUrl);
            }
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

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
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

            ApiResponseSocialMedia = null;
            base.Dispose();
        }
    }
    
}