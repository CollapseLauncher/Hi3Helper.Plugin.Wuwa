using System.Net;
using System.Security.Cryptography;
using Hi3Helper.Plugin.Wuwa.Management;

string root = Path.Combine(Path.GetTempPath(), "wuwa-update-safety-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    Check(WuwaKnownHotfixPatch.FindResourcePackageVersion(root, "3.6.1") == null, "Missing resources");
    Mount("3.6.0");
    Check(WuwaKnownHotfixPatch.FindResourcePackageVersion(root, "3.6.1") == "3.6.0", "Maintenance package keeps internal resource root");
    Check(WuwaKnownHotfixPatch.FindResourcePackageVersion(root, "3.7.0") == null, "Do not use previous major/minor resources");

    using var client = new HttpClient(new MetadataHandler());
    var patch = await WuwaKnownHotfixPatch.DiscoverAsync(root, "3.6.1", client, default)
        ?? throw new Exception("Hotfix not discovered");
    Check(patch.PackageVersion == "3.6.1" && patch.ResourcePackageVersion == "3.6.0", "Keep package identities separate");
    Check(patch.CanApply(root), "Discovered patch can apply to mounted source");
    Check(patch.GetPackageRoot(root).EndsWith(Path.Combine("Resources", "3.6.0")), "Patch writes to internal resource root");
    Check(patch.Files.Select(x => x.Name).ToHashSet().SetEquals(new[]
    {
        "ManifestLauncher_g0_3.6.13_3.6.14.hp",
        "ManifestLauncher_ls_3.6.13_3.6.14.hp"
    }), "Collect both Launcher groups from observed update");

    foreach (bool malformed in new[] { false, true })
    {
        using var failedClient = new HttpClient(new MetadataHandler(failGroup: true, malformed: malformed));
        await ExpectFailure(() => WuwaKnownHotfixPatch.DiscoverAsync(root, "3.6.1", failedClient, default),
            "Incomplete discovery must fail on HTTP/metadata errors");
    }
    Mount("3.6.1");
    await ExpectFailure(() => Task.FromResult(WuwaKnownHotfixPatch.FindResourcePackageVersion(root, "3.6.1")),
        "Ambiguous mounted resource roots are rejected");

    string destination = Path.Combine(root, "installed.bin");
    string staged = Path.Combine(root, "staged.bin");
    byte[] good = [1, 2, 3, 4];
    string hash = Convert.ToHexString(MD5.HashData(good));
    await File.WriteAllTextAsync(destination, "original");
    foreach (byte[] bad in new byte[][] { [9, 2, 3, 4], [1, 2] })
    {
        await File.WriteAllBytesAsync(staged, bad);
        await ExpectFailure(() => WuwaFileIntegrity.CommitAsync(staged, destination, 4, hash, default),
            "Reject bad replacement hash/size");
        Check(await File.ReadAllTextAsync(destination) == "original", "Failed verification preserves installed bytes");
    }
    await ExpectFailure(() => WuwaFileIntegrity.VerifyAsync(Path.Combine(root, "missing"), 4, hash, default),
        "Missing required file aborts verification");
    await File.WriteAllBytesAsync(staged, good);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await ExpectFailure(() => WuwaFileIntegrity.CommitAsync(staged, destination, 4, hash, cts.Token),
        "Cancellation preserves installed file");
    Check(await File.ReadAllTextAsync(destination) == "original", "Cancelled commit preserves installed bytes");
    await WuwaFileIntegrity.CommitAsync(staged, destination, 4, hash, default);
    Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(good), "Valid replacement commits");
    Check(!File.Exists(staged), "Successful commit consumes staging file");
}
finally
{
    Directory.Delete(root, recursive: true);
}

void Mount(string package)
{
    string packageRoot = Path.Combine(root, "Client", "Saved", "Resources", package);
    Directory.CreateDirectory(Path.Combine(packageRoot, "Launcher", "3.6.13"));
    Directory.CreateDirectory(Path.Combine(packageRoot, "Mount"));
    File.WriteAllText(Path.Combine(packageRoot, "Mount", "MountLauncher.txt"),
        "::Mount::\nLauncher/3.6.13/pakchunk1,17,hash,,,\n::Del::\n");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

static async Task ExpectFailure(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
    {
        Console.WriteLine("PASS " + name);
        return;
    }
    throw new Exception("Expected failure: " + name);
}

sealed class MetadataHandler(bool failGroup = false, bool malformed = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
        bool exists = name is "ManifestLauncher_g0_3.6.13_3.6.14.hp"
            or "ManifestLauncher_ls_3.6.13_3.6.14.hp" or "ManifestLauncher.txt";
        if (failGroup && name.StartsWith("ManifestLauncher_g0"))
        {
            if (!malformed)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        var response = new HttpResponseMessage(exists ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        if (exists)
        {
            response.Content = new ByteArrayContent(new byte[10]);
            response.Headers.TryAddWithoutValidation("X-Cos-Meta-Md5", new string('a', 32));
        }
        return Task.FromResult(response);
    }
}
