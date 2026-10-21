using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management
{
    // Partial declaration containing shared download helpers used by both Install and Patch flows.
    internal partial class WuwaGameInstaller
    {
        internal async Task TryDownloadWholeFileWithFallbacksAsync(Uri originalUri, string outputPath, string rawDest, CancellationToken token, Action<long>? progressCallback)
        {
            long attemptBytes = 0;
            void ReportAttempt(long bytes)
            {
                attemptBytes += bytes;
                progressCallback?.Invoke(bytes);
            }

            // Try original first
            try
            {
                await DownloadWholeFileAsync(originalUri, outputPath, token, ReportAttempt).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // The fallback reports the entire resumed prefix again, including bytes
                // credited before this attempt. Undo only this file's contribution.
                if (attemptBytes != 0)
                    progressCallback?.Invoke(-attemptBytes);
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Primary download failed: {Uri}. Reason: {Msg}", originalUri, ex.Message);
            }

            // Fallback 1: Re-encode each path segment of the original URI to handle special chars
            try
            {
                string reEncodedUrl = ReEncodeUriPathSegments(originalUri);
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Trying re-encoded fallback URI: {Uri}", reEncodedUrl);
                Uri fallbackUri = new Uri(reEncodedUrl, UriKind.Absolute);
                await DownloadWholeFileAsync(fallbackUri, outputPath, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadWholeFileWithFallbacksAsync] Re-encoded fallback failed: {Msg}", ex.Message);
            }

            throw new HttpRequestException($"All download attempts failed for: {rawDest}");
        }

        internal async Task TryDownloadChunkedFileWithFallbacksAsync(Uri originalUri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, string rawDest, CancellationToken token, Action<long>? progressCallback)
        {
            long attemptBytes = 0;
            void ReportAttempt(long bytes)
            {
                attemptBytes += bytes;
                progressCallback?.Invoke(bytes);
            }

            // Try original first
            try
            {
                await DownloadChunkedFileAsync(originalUri, outputPath, chunkInfos, token, ReportAttempt).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // Completed chunks and a partial chunk are credited again on resume.
                if (attemptBytes != 0)
                    progressCallback?.Invoke(-attemptBytes);
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Primary chunked download failed: {Uri}. Reason: {Msg}", originalUri, ex.Message);
            }

            // Fallback 1: Re-encode each path segment of the original URI to handle special chars
            try
            {
                string reEncodedUrl = ReEncodeUriPathSegments(originalUri);
                SharedStatic.InstanceLogger.LogDebug("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Trying re-encoded fallback URI: {Uri}", reEncodedUrl);
                Uri fallbackUri = new Uri(reEncodedUrl, UriKind.Absolute);
                await DownloadChunkedFileAsync(fallbackUri, outputPath, chunkInfos, token, progressCallback).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                SharedStatic.InstanceLogger.LogWarning("[WuwaGameInstaller::TryDownloadChunkedFileWithFallbacksAsync] Re-encoded fallback failed: {Msg}", ex.Message);
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

        /// <summary>
        /// Re-encodes each path segment of an existing URI. This handles cases where
        /// the original URI has un-encoded special characters (spaces, CJK, etc.)
        /// that cause the CDN to 404. Does NOT produce a doubled path — it only
        /// re-encodes what's already in the URI.
        /// </summary>
        internal static string ReEncodeUriPathSegments(Uri uri)
        {
            string authority = uri.GetLeftPart(UriPartial.Authority);
            // Use decoded AbsolutePath so we get the raw characters, then re-encode each segment
            string decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            string[] segments = decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string encodedPath = string.Join("/", segments.Select(Uri.EscapeDataString));
            return $"{authority}/{encodedPath}";
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
                        var range = resp.Content.Headers.ContentRange;
                        if (range?.Unit != "bytes" || range.From != existingLength ||
                            range.To == null || range.Length == null || range.To != range.Length - 1)
                            throw new InvalidDataException("Invalid Content-Range on resumed download.");
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

                if (!resuming && resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
                    throw new InvalidDataException("Unexpected partial response for a full download.");

                FileMode fileMode = resuming ? FileMode.Append : FileMode.Create;
                await using FileStream fs = new(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);

                if (resuming)
                {
                    progressCallback?.Invoke(existingLength);
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                long received = 0;
                nint speedLimiterContext = SpeedLimiterService.CreateServiceContext();
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        received += read;
                        progressCallback?.Invoke(read);
                        await SpeedLimiterService.AddBytesOrWaitAsync(speedLimiterContext, read, token).ConfigureAwait(false);
                    }
                    long? expected = resuming
                        ? resp.Content.Headers.ContentRange!.Length - existingLength
                        : resp.Content.Headers.ContentLength;
                    if (expected.HasValue && received != expected.Value)
                        throw new InvalidDataException("Incomplete download response.");
                }
                finally
                {
                    SpeedLimiterService.FreeServiceContext(speedLimiterContext);
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

        private static async Task<bool> ChunkMatchesAsync(FileStream stream, long start, long length,
            string md5, byte[] buffer, CancellationToken token)
        {
            stream.Position = start;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            while (length > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
                if (read == 0) return false;
                hash.AppendData(buffer, 0, read);
                length -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).Equals(md5, StringComparison.OrdinalIgnoreCase);
        }

        internal async Task DownloadChunkedFileAsync(Uri uri, string outputPath, WuwaApiResponseResourceChunkInfo[] chunkInfos, CancellationToken token, Action<long>? progressCallback)
        {
            string tempPath = outputPath + ".tmp";

            if (chunkInfos.Length == 0)
                throw new InvalidDataException("Chunk list is empty.");
            long expectedStart = 0;
            foreach (var chunk in chunkInfos)
            {
                if (chunk.Start != (ulong)expectedStart || chunk.End < chunk.Start || chunk.End >= long.MaxValue)
                    throw new InvalidDataException("Chunks must be contiguous and ordered.");
                expectedStart = checked((long)chunk.End + 1);
            }

            await using (var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (fs.Length > expectedStart)
                    fs.SetLength(expectedStart);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                nint speedLimiterContext = SpeedLimiterService.CreateServiceContext();
                try
                {
                    foreach (var chunk in chunkInfos)
                    {
                        token.ThrowIfCancellationRequested();
                        long start = (long)chunk.Start;
                        long end = (long)chunk.End;
                        long length = end - start + 1;
                        long available = Math.Clamp(fs.Length - start, 0, length);
                        bool hasHash = !string.IsNullOrEmpty(chunk.Md5);
                        if (available == length && (!hasHash || await ChunkMatchesAsync(fs, start, length, chunk.Md5!, buffer, token).ConfigureAwait(false)))
                        {
                            progressCallback?.Invoke(length);
                            continue;
                        }

                        // A hashed chunk is the resume boundary. Re-fetch only this chunk
                        // when its cached bytes cannot be verified; preserve later good chunks.
                        long resume = hasHash ? 0 : available;
                        progressCallback?.Invoke(resume);
                        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start + resume, end);
                        using var resp = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        var range = resp.Content.Headers.ContentRange;
                        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                            range?.Unit != "bytes" || range.From != start + resume || range.To != end ||
                            range.Length != expectedStart)
                            throw new InvalidDataException($"Invalid range response for {start + resume}-{end}.");

                        await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        fs.Position = start + resume;
                        long remaining = length - resume;
                        while (remaining > 0)
                        {
                            int read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                            if (read == 0)
                                throw new EndOfStreamException("Incomplete chunk response.");
                            await fs.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            remaining -= read;
                            progressCallback?.Invoke(read);
                            await SpeedLimiterService.AddBytesOrWaitAsync(speedLimiterContext, read, token).ConfigureAwait(false);
                        }
                        if (await content.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                            throw new InvalidDataException("Chunk response exceeds the requested range.");
                        if (hasHash && !await ChunkMatchesAsync(fs, start, length, chunk.Md5!, buffer, token).ConfigureAwait(false))
                            throw new InvalidDataException($"Chunk MD5 mismatch for {start}-{end}.");
                    }
                    fs.SetLength(expectedStart);
                }
                finally
                {
                    SpeedLimiterService.FreeServiceContext(speedLimiterContext);
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
