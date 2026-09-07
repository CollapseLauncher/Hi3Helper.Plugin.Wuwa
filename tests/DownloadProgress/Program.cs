using System.Net;
using Hi3Helper.Plugin.Wuwa.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;

// Exercise the real download helpers with deterministic HTTP failures and real temp files.
foreach (bool chunked in new[] { false, true })
foreach (int existing in new[] { 0, 2 })
foreach (int failOnRequest in chunked ? new[] { 1, 2 } : new[] { 1 })
foreach (bool restart in chunked ? new[] { false } : new[] { false, true })
{
    string directory = Path.Combine(Path.GetTempPath(), "wuwa-progress-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try
    {
        byte[] data = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        string output = Path.Combine(directory, "file.bin");
        if (existing > 0)
            await File.WriteAllBytesAsync(output + ".tmp", data[..existing]);
        using var handler = new RetryHandler(data, restart, failOnRequest);
        using var client = new HttpClient(handler);
        var installer = new WuwaGameInstaller(client);
        long progress = 0;
        long peak = 0;
        void Report(long delta)
        {
            progress += delta;
            peak = Math.Max(peak, progress);
            if (progress < 0) throw new Exception("Progress became negative.");
        }

        Uri uri = new("https://example.test/file.bin");
        if (chunked)
            await installer.TryDownloadChunkedFileWithFallbacksAsync(uri, output,
                [new() { Start = 0, End = 5 }, new() { Start = 6, End = 11 }],
                "file.bin", default, Report);
        else
            await installer.TryDownloadWholeFileWithFallbacksAsync(uri, output, "file.bin", default, Report);

        if (!(await File.ReadAllBytesAsync(output)).SequenceEqual(data))
            throw new Exception("Downloaded contents differ.");
        if (progress != data.Length || peak > data.Length)
            throw new Exception($"Progress overcount: final={progress}, peak={peak}, expected={data.Length}.");
        Console.WriteLine($"PASS chunked={chunked}, existing={existing}, restart={restart}, failed request={failOnRequest}");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

sealed class RetryHandler(byte[] data, bool restart, int failOnRequest) : HttpMessageHandler
{
    private int _requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        int call = ++_requests;
        var range = request.Headers.Range?.Ranges.Single();
        int start = (int)(range?.From ?? 0);
        int end = (int)(range?.To ?? data.Length - 1);
        bool ignoreRange = restart && call == 2;
        if (ignoreRange) start = 0;
        Stream body = call == failOnRequest
            ? new FailingStream(data[start..(start + 3)])
            : new MemoryStream(data[start..(end + 1)]);
        return Task.FromResult(new HttpResponseMessage(range == null || ignoreRange
            ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new StreamContent(body) });
    }
}

sealed class FailingStream(byte[] bytes) : MemoryStream(bytes)
{
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => Position == Length
            ? Task.FromException<int>(new IOException("Simulated mid-download disconnect."))
            : base.ReadAsync(buffer, offset, count, token);
}

namespace Hi3Helper.Plugin.Wuwa.Management
{
    internal partial class WuwaGameInstaller(HttpClient client)
    {
        private readonly HttpClient _downloadHttpClient = client;
    }
}
