using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management
{
    // Partial declaration containing shared download helpers used by both Install and Patch flows.
    internal partial class WuwaGameInstaller
    {
        internal async Task TryDownloadWholeFileWithFallbacksAsync(Uri originalUri, string outputPath, string rawDest, CancellationToken token, Action<long>? progressCallback)
        {
            // Try original first
            try
            {
                await DownloadWholeFileAsync(originalUri, outputPath, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Primary download failed: {Uri}. Reason: {Msg}", originalUri, ex.Message);
            }

            string encodedPath = EncodePathSegments(rawDest);

            // Fallback 1: encoded concatenation using the Path portion of the original URI
            try
            {
                var basePath = originalUri.GetLeftPart(UriPartial.Path);
                string encodedConcatUrl = basePath.TrimEnd('/') + "/" + encodedPath;
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Trying encoded concatenation fallback URI: {Uri}", encodedConcatUrl);
                Uri fallbackUri = new Uri(encodedConcatUrl, UriKind.Absolute);
                await DownloadWholeFileAsync(fallbackUri, outputPath, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Encoded concatenation fallback failed: {Msg}", ex.Message);
            }

            // Fallback 2: try using a simple concatenation (encoded)
            try
            {
                var baseAuthority = originalUri.GetLeftPart(UriPartial.Authority);
                var baseDir = originalUri.AbsolutePath;
                int lastSlash = baseDir.LastIndexOf('/');
                if (lastSlash >= 0)
                    baseDir = baseDir[..(lastSlash + 1)];
                string tryUrl = baseAuthority + baseDir + encodedPath;
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Trying authority+dir fallback URI: {Uri}", tryUrl);
                Uri fallbackUri2 = new Uri(tryUrl, UriKind.Absolute);
                await DownloadWholeFileAsync(fallbackUri2, outputPath, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Authority+dir fallback failed: {Msg}", ex.Message);
            }

            throw new HttpRequestException($"All download attempts failed for: {rawDest}");
        }

        internal async Task TryDownloadChunkedFileWithFallbacksAsync(Uri originalUri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, string rawDest, CancellationToken token, Action<long>? progressCallback)
        {
            // Try original first
            try
            {
                await DownloadChunkedFileAsync(originalUri, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Primary chunked download failed: {Uri}. Reason: {Msg}", originalUri, ex.Message);
            }

            string encodedPath = EncodePathSegments(rawDest);

            // Fallback 1: encoded concatenation using the Path portion of the original URI
            try
            {
                var basePath = originalUri.GetLeftPart(UriPartial.Path);
                string encodedConcatUrl = basePath.TrimEnd('/') + "/" + encodedPath;
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Trying encoded concatenation fallback URI: {Uri}", encodedConcatUrl);
                Uri fallbackUri = new Uri(encodedConcatUrl, UriKind.Absolute);
                await DownloadChunkedFileAsync(fallbackUri, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Encoded concatenation fallback failed: {Msg}", ex.Message);
            }

            // Fallback 2: authority+dir + encoded path
            try
            {
                var baseAuthority = originalUri.GetLeftPart(UriPartial.Authority);
                var baseDir = originalUri.AbsolutePath;
                int lastSlash = baseDir.LastIndexOf('/');
                if (lastSlash >= 0)
                    baseDir = baseDir[..(lastSlash + 1)];
                string tryUrl = baseAuthority + baseDir + encodedPath;
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Trying authority+dir fallback URI: {Uri}", tryUrl);
                Uri fallbackUri2 = new Uri(tryUrl, UriKind.Absolute);
                await DownloadChunkedFileAsync(fallbackUri2, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Authority+dir fallback failed: {Msg}", ex.Message);
            }

            throw new HttpRequestException($"All chunked download attempts failed for: {rawDest}");
        }

        internal static string EncodePathSegments(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            string[] parts = path.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            return string.Join("/", parts.Select(Uri.EscapeDataString));
        }

        internal async Task DownloadWholeFileAsync(Uri uri, string outputPath, CancellationToken token, Action<long>? progressCallback)
        {
            string tempPath = outputPath + ".tmp";

            long existingLength = 0;
            if (File.Exists(tempPath))
            {
                existingLength = new FileInfo(tempPath).Length;
            }

            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Downloading {Uri} -> {Temp} (resume from {Existing} bytes)", uri, tempPath, existingLength);

            HttpResponseMessage? resp = null;
            bool resuming = false;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (existingLength > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                }

                resp = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (existingLength > 0)
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                    {
                        resuming = true;
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::DownloadWholeFileAsync] Server returned 416 for Range: bytes={Existing}-; deleting stale .tmp and re-downloading from scratch", existingLength);
                        resp.Dispose();
                        resp = null;
                        try { File.Delete(tempPath); } catch { }
                        existingLength = 0;

                        var freshRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                        resp = await _downloadHttpClient.SendAsync(freshRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    }
                    else
                    {
                        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Server returned {Status} instead of 206; restarting download from scratch", resp.StatusCode);
                        existingLength = 0;
                    }
                }

                if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    string body = string.Empty;
                    try { body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                    catch { /* ignored */ }

                    SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadWholeFileAsync] Failed GET {Uri}: {Status}. Body preview: {BodyPreview}", uri, resp.StatusCode, body.Length > 200 ? body[..200] + "..." : body);
                    throw new HttpRequestException($"Failed to GET {uri} : {(int)resp.StatusCode} {resp.StatusCode}", null, resp.StatusCode);
                }

                await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                FileMode fileMode = resuming ? FileMode.Append : FileMode.Create;
                await using FileStream fs = new(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);

                if (resuming)
                {
                    progressCallback?.Invoke(existingLength);
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        progressCallback?.Invoke(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                resp?.Dispose();
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadWholeFileAsync] Moved {Temp} -> {Out}", tempPath, outputPath);
        }

        internal async Task DownloadChunkedFileAsync(Uri uri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, CancellationToken token, Action<long>? progressCallback)
        {
            string tempPath = outputPath + ".tmp";

            long existingLength = 0;
            if (File.Exists(tempPath))
            {
                existingLength = new FileInfo(tempPath).Length;
            }

            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Downloading chunks for {Uri} -> {Temp} (resume from {Existing} bytes)", uri, tempPath, existingLength);

            FileMode fileMode = existingLength > 0 ? FileMode.Append : FileMode.Create;
            await using (var fs = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    foreach (var chunk in chunkInfos)
                    {
                        token.ThrowIfCancellationRequested();

                        long start = (long)chunk.Start;
                        long end = (long)chunk.End;
                        long chunkSize = end - start + 1;

                        if (existingLength > 0 && existingLength >= end + 1)
                        {
                            progressCallback?.Invoke(chunkSize);
                            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Skipping already-written chunk {Start}-{End}", start, end);
                            continue;
                        }

                        long resumeOffset = 0;
                        if (existingLength > 0 && existingLength > start)
                        {
                            resumeOffset = existingLength - start;
                            progressCallback?.Invoke(resumeOffset);
                            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Resuming chunk {Start}-{End} from offset {Offset}", start, end, resumeOffset);
                        }

                        var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start + resumeOffset, end);

                        using HttpResponseMessage resp = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        {
                            string body = string.Empty;
                            try { body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                            catch { /* ignored */ }

                            SharedStatic.InstanceLogger.LogError("[WuwaGameInstaller::DownloadChunkedFileAsync] Failed GET {Uri} (range {Start}-{End}): {Status}. Body preview: {BodyPreview}", uri, start + resumeOffset, end, resp.StatusCode, body.Length > 200 ? body[..200] + "..." : body);
                            throw new HttpRequestException($"Failed to GET {uri} range {start + resumeOffset}-{end} : {(int)resp.StatusCode} {resp.StatusCode}", null, resp.StatusCode);
                        }

                        await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                        int read;
                        while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                            progressCallback?.Invoke(read);
                        }

                        SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Wrote chunk {Start}-{End} to temp", start, end);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            SharedStatic.InstanceLogger.LogTrace("[WuwaGameInstaller::DownloadChunkedFileAsync] Moved {Temp} -> {Out}", tempPath, outputPath);
        }
    }
}
