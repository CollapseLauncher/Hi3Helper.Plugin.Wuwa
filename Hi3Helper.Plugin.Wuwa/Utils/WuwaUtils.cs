using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

namespace Hi3Helper.Plugin.Wuwa.Utils;

internal static class WuwaUtils
{
    internal static HttpClient CreateApiHttpClient(string? apiBaseUrl = null, string? gameTag = null, string? authCdnToken = "", string? apiOptions = "", string? hash1 = "")
        => CreateApiHttpClientBuilder(apiBaseUrl, gameTag, authCdnToken, apiOptions, hash1).Create();

    private static PluginHttpClientBuilder CreateApiHttpClientBuilder(string? apiBaseUrl, string? gameTag = null, string? authCdnToken= "", string? accessOption = null, string? hash1 = "")
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
        string resultString;
        try
        {
            bool isAsterite2Sufficient =
                Encoding.UTF8.TryGetBytes(whatDaDup, wannaConvene, out int amountOfCryFromBegging);
            SharedStatic.InstanceLogger.LogDebug(
                "[WuwaUtils::AeonPlsHelpMe] Attempting to decode string using AeonPlsHelpMe. Input: {Input}, BufferSize: {BufferSize}, IsBufferSufficient: {IsBufferSufficient}, EncodedLength: {EncodedLength}",
                whatDaDup, wannaConvene.Length, isAsterite2Sufficient, amountOfCryFromBegging);

            // Try Base64Url decode in-place. If decode returns 0 it means input wasn't Base64Url encoded.
            int decodedLen = 0;
            try
            {
                decodedLen = Base64Url.DecodeFromUtf8InPlace(wannaConvene[..amountOfCryFromBegging]);
            }
            catch
            {
                // Decode routine may throw for invalid inputs on some runtimes/implementations.
                decodedLen = 0;
            }

            if (!isAsterite2Sufficient)
            {
                // Buffer wasn't large enough for encoding step; fall back to original input.
                SharedStatic.InstanceLogger.LogError(
                    "[WuwaUtils::AeonPlsHelpMe] Buffer too small while preparing bytes for decoding. Input: {Input}",
                    whatDaDup);
                resultString = whatDaDup;
            }
            else if (decodedLen == 0)
            {
                // Input is not Base64Url encoded — treat as already-decoded (plain token or URL).
                SharedStatic.InstanceLogger.LogInformation(
                    "[WuwaUtils::AeonPlsHelpMe] Input appears already decoded; returning original value.");
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaUtils::AeonPlsHelpMe] Already-decoded input (debug): {Input}",
                    whatDaDup);
                resultString = whatDaDup;
            }
            else
            {
                // We have decoded bytes; apply XOR transform and return result.
                int transformedLen = transform.TransformBlockCore(wannaConvene[..decodedLen], wannaConvene);

                // Log raw decoded bytes (hex) and the decoded string before sanitization to help diagnose missing characters.
                try
                {
                    SharedStatic.InstanceLogger.LogTrace(
                        "[WuwaUtils::AeonPlsHelpMe] Decoded bytes (hex): {Hex}",
                        Convert.ToHexString(wannaConvene[..transformedLen]));
                }
                catch
                {
                    // best-effort logging only
                }

                resultString = Encoding.UTF8.GetString(wannaConvene[..transformedLen]);

                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaUtils::AeonPlsHelpMe] Successfully decoded string using AeonPlsHelpMe. Result: {Result}",
                    resultString);
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                "[WuwaUtils::AeonPlsHelpMe] Failed to decode string using AeonPlsHelpMe. Input: {Input}. Error: {Error}",
                whatDaDup, ex.Message);
            resultString = whatDaDup;
        }
        finally
        {
            if (iWannaConvene != null)
            {
                ArrayPool<byte>.Shared.Return(iWannaConvene);
            }
        }

        // Sanitization: remove CR/LF and trim ends first
        string beforeSanitize = resultString;
        string sanitized = resultString.Replace("\r", "").Replace("\n", "").Trim();

        // Remove any internal whitespace (including indentation introduced by remote responses)
        // This prevents stray spaces/newlines from breaking tokens when concatenated into URLs.
        if (sanitized.Length > 0)
        {
            ReadOnlySpan<char> sspan = sanitized.AsSpan();
            char[] buf = new char[sspan.Length];
            int di = 0;
            for (int i = 0; i < sspan.Length; i++)
            {
                if (!char.IsWhiteSpace(sspan[i]))
                    buf[di++] = sspan[i];
            }

            if (di != sanitized.Length)
            {
                string removedWs = new string(buf, 0, di);
                SharedStatic.InstanceLogger.LogInformation(
                    "[WuwaUtils::AeonPlsHelpMe] Sanitization removed whitespace from decoded string. Before: {BeforePreview}, After: {AfterPreview}",
                    beforeSanitize.Length <= 80 ? beforeSanitize : beforeSanitize[..80] + "...",
                    removedWs.Length <= 80 ? removedWs : removedWs[..80] + "...");
                sanitized = removedWs;
            }
        }

        if (sanitized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            sanitized = sanitized["http://".Length..];
        else if (sanitized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            sanitized = sanitized["https://".Length..];

        sanitized = sanitized.TrimEnd('/');

        return sanitized;
    }

    internal static string ComputeMd5Hex(Stream stream, CancellationToken token = default)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var md5 = MD5.Create();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10); // 64 KiB buffer
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer)) > 0)
            {
                md5.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            md5.TransformFinalBlock(buffer, 0, 0);

            byte[] hash = md5.Hash!;
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }


    internal static async ValueTask<string> ComputeMd5HexAsync(Stream stream, CancellationToken token = default)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var md5 = MD5.Create();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10); // 64 KiB buffer
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                md5.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            md5.TransformFinalBlock(buffer, 0, 0);

            byte[] hash = md5.Hash!;
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

