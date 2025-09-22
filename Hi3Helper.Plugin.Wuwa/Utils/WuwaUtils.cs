using Hi3Helper.Plugin.Core.Utility;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Net.Http;
using System.Text;

namespace Hi3Helper.Plugin.Wuwa.Utils;

internal static class WuwaUtils
{
    internal static HttpClient CreateApiHttpClient(string? apiBaseUrl = null, string? gameTag = null, string? authCdnToken = "", string? apiOptions = "", string? hash1 = "")
        => CreateApiHttpClientBuilder(apiBaseUrl, gameTag, authCdnToken, apiOptions, hash1).Create();

    internal static PluginHttpClientBuilder CreateApiHttpClientBuilder(string? apiBaseUrl, string? gameTag = null, string? authCdnToken= "", string? accessOption = null, string? hash1 = "")
    {
        PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
            .SetUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");

        // ReSharper disable once ConvertIfStatementToSwitchStatement      
        if (authCdnToken == null)
        {
            throw new ArgumentNullException(nameof(authCdnToken), "authCdnToken cannot be empty. Use string.Empty if you want to ignore it instead.");
        }

        if (!string.IsNullOrEmpty(authCdnToken))
        {
            authCdnToken = authCdnToken.AeonPlsHelpMe();
            // authCdnToken.Aggregate(string.Empty, (current, c) => current + (char)(c ^ 99));
            // authCdnToken = Convert.FromBase64String(authCdnToken).Aggregate(string.Empty, (current, b) => current + (char)(b ^ 99));
#if DEBUG
            SharedStatic.InstanceLogger.LogTrace("Decoded authCdnToken: {}", authCdnToken);
#endif
        }

        switch (accessOption)
        {
            case "news":
                builder.SetBaseUrl(apiBaseUrl.CombineUrlFromString("launcher", authCdnToken, gameTag, "information", "en.json"));
                break;
            case "bg":
                builder.SetBaseUrl(apiBaseUrl.CombineUrlFromString("launcher", authCdnToken, gameTag, "background", hash1, "en.json"));
                break;
            case "media":
                builder.SetBaseUrl(apiBaseUrl.CombineUrlFromString("launcher", gameTag, authCdnToken, "social", "en.json"));
                break;
            default:
                break;
        }


#if DEBUG
        SharedStatic.InstanceLogger.LogTrace("Created HttpClient with Token: {}", authCdnToken);
#endif
        string hostname = builder.HttpBaseUri?.Host ?? ""; // exclude "https://"
        builder.AddHeader("Host", hostname);

        return builder;
    }

    internal static string AeonPlsHelpMe(this string whatDaDup)
    {
        const int amountOfBeggingForHelp = 4096;

        WuwaTransform transform = new(99);
        int bufferSize = Encoding.UTF8.GetMaxByteCount(whatDaDup.Length);

        byte[]? iWannaConvene = bufferSize <= amountOfBeggingForHelp
            ? null
            : ArrayPool<byte>.Shared.Rent(bufferSize);

        scoped Span<byte> wannaConvene = iWannaConvene ?? stackalloc byte[bufferSize];
        try
        {
            bool isAsterite2Sufficient =
                Encoding.UTF8.TryGetBytes(whatDaDup, wannaConvene, out int amountOfCryFromBegging);
            amountOfCryFromBegging = Base64Url.DecodeFromUtf8InPlace(wannaConvene[..amountOfCryFromBegging]);

            if (!isAsterite2Sufficient || amountOfCryFromBegging == 0)
            {
                throw new InvalidOperationException();
            }

            amountOfCryFromBegging = transform.TransformBlockCore(wannaConvene[..amountOfCryFromBegging], wannaConvene);

            return Encoding.UTF8.GetString(wannaConvene[..amountOfCryFromBegging]);
        }
        finally
        {
            if (iWannaConvene != null)
            {
                ArrayPool<byte>.Shared.Return(iWannaConvene);
            }
        }
    }
}

