using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;

// Exercise the production manager/installer. Only HTTP responses and game files are fixtures.
var assembly = typeof(WuwaApiResponseContext).Assembly;
var managerType = assembly.GetType("Hi3Helper.Plugin.Wuwa.Management.WuwaGameManager", true)!;
var installerType = assembly.GetType("Hi3Helper.Plugin.Wuwa.Management.WuwaGameInstaller", true)!;
var patchType = installerType.GetNestedType("Patch", BindingFlags.NonPublic)!;
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
string scratch = Path.Combine(Path.GetTempPath(), "wuwa-compatibility-" + Guid.NewGuid());
Directory.CreateDirectory(scratch);
byte[] data = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
int passed = 0;
string Hash(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));
string Manifest(string name, byte[] bytes, string? fromFolder = null) => JsonSerializer.Serialize(new
{
    resource = new[] { new { dest = name, size = bytes.Length, md5 = Hash(bytes), fromFolder } }
});
string Pack(string index, string manifest, string basis = "base", string version = "3.7.0", string? patch = null) =>
    JsonSerializer.Serialize(new { version, indexFile = index, indexFileMd5 = Hash(Encoding.UTF8.GetBytes(manifest)), baseUrl = basis })[..^1] +
    (patch == null ? "}" : ",\"patchConfig\":[" + patch + "]}");
string V1(string pack, bool preload = false) => preload
    ? "{\"default\":{\"config\":{\"version\":\"3.6.1\",\"indexFile\":\"current.json\",\"baseUrl\":\"base\"}},\"predownload\":{\"config\":" + pack + "}}"
    : "{\"default\":{\"config\":" + pack + "}}";
string V2(string first, string second, bool preload = false) =>
    "{\"bundles\":{\"default\":{\"resourcePacks\":[\"base\",\"audio\"]}},\"resourcePacks\":{\"base\":" + first + ",\"audio\":" + second + "}" +
    (preload ? ",\"predownload\":{\"bundles\":{\"default\":{\"resourcePacks\":[\"base\",\"audio\"]}},\"resourcePacks\":{\"base\":" + first + ",\"audio\":" + second + "}}" : "") + "}";
HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Set(object instance, string name, object? value)
{
    for (Type? type = instance.GetType(); type != null; type = type.BaseType)
    {
        var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
        if (property != null) { property.SetValue(instance, value); return; }
        var field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
        if (field != null) { field.SetValue(instance, value); return; }
    }
    throw new Exception("Missing member: " + name);
}
object? Call(object? instance, Type type, string method, params object?[] arguments)
{
    try { return type.GetMethod(method, flags)!.Invoke(instance, arguments); }
    catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
}
Task Invoke(object instance, string method, params object?[] arguments) => (Task)Call(instance, instance.GetType(), method, arguments)!;
async Task<Exception?> Error(Func<Task> action) { try { await action(); return null; } catch (Exception ex) { return ex; } }
async Task Test(string name, Func<Task> action)
{
    if (args.Length > 0 && !args.Any(filter => name.Contains(filter, StringComparison.OrdinalIgnoreCase))) return;
    await action(); passed++; Console.WriteLine("PASS " + name);
}
string Fresh() { string dir = Path.Combine(scratch, Guid.NewGuid().ToString()); Directory.CreateDirectory(dir); return dir; }
object Manager(HttpClient client, string dir, string version = "3.6.1")
{
    object result = Activator.CreateInstance(managerType, flags, null,
        ["game.exe", "https://example.test/", "VlNTU1c8DAEsKzslESUCDRIQAiomLA4WKBEMIAABOQgyMSEgVAA", "JFJWUA", ""], null)!;
    Set(result, "ApiResponseHttpClient", client);
    ((IGameManager)result).SetGamePath(dir);
    Set(result, "CurrentGameVersion", new GameVersion(version));
    return result;
}
object Installer(object manager, HttpClient client)
{
    object result = Activator.CreateInstance(installerType, flags, null, [manager], null)!;
    Set(result, "_downloadHttpClient", client);
    return result;
}
Task Run(object installer, GameInstallerKind kind, CancellationToken token = default) => kind == GameInstallerKind.Install
    ? Invoke(installer, "StartInstallCoreAsync", kind, null, null, token)
    : Invoke(installer, "StartPatchCoreAsync", kind, kind == GameInstallerKind.Preload, null, null, token);
try
{
    await Test("preserve bundles and unknown launcher fields", () =>
    {
        string dir = Fresh(), path = Path.Combine(dir, "launcherDownloadConfig.json");
        string original = """{"appId":"kept","custom":{"value":42},"bundles":{"default":{"version":"3.6.1","resourcePacks":["base"]},"other":{"state":"repairing"}}}""";
        File.WriteAllText(path, original);
        using var client = new HttpClient(new Handler(_ => throw new Exception("Unexpected request")));
        Call(Manager(client, dir), managerType, "TryUpdateLauncherDownloadConfig", dir, "3.7.0");
        var actual = JsonNode.Parse(File.ReadAllText(path))!;
        var before = JsonNode.Parse(original)!;
        Assert(JsonNode.DeepEquals(actual["bundles"], before["bundles"]) && JsonNode.DeepEquals(actual["custom"], before["custom"]), "Metadata lost");
        Assert((string?)actual["version"] == "3.7.0", "Version not updated");
        return Task.CompletedTask;
    });

    foreach (string mode in new[] { "healthy", "corrupt-cache", "ignored-range", "wrong-range", "short-body", "extra-body", "bad-chunk-hash" })
    await Test("chunk validation: " + mode, async () =>
    {
        string path = Path.Combine(Fresh(), "file.bin");
        if (mode == "corrupt-cache") await File.WriteAllBytesAsync(path + ".tmp", [255, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        var requests = new List<long>();
        using var client = new HttpClient(new Handler(request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            int start = (int)range.From!.Value, end = (int)range.To!.Value;
            requests.Add(start);
            if (mode == "ignored-range") return Bytes(data);
            byte[] body = data[start..(end + 1)];
            if (mode == "short-body") body = body[..3];
            if (mode == "extra-body") body = [..body, 255];
            if (mode == "bad-chunk-hash") body[0] = 255;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentRange = mode == "wrong-range" ? new(1, 6, 12) : new(start, end, 12);
            return response;
        }));
        var installer = Installer(Manager(client, Fresh()), client);
        WuwaApiResponseResourceChunkInfo[] chunks = [new() { Start = 0, End = 5, Md5 = Hash(data[..6]) }, new() { Start = 6, End = 11, Md5 = Hash(data[6..]) }];
        var error = await Error(() => Invoke(installer, "DownloadChunkedFileAsync", new Uri("https://example.test/file"), path, chunks, CancellationToken.None, null));
        if (mode is "healthy" or "corrupt-cache")
        {
            Assert(error == null && File.ReadAllBytes(path).SequenceEqual(data), "Valid download failed: " + error);
            if (mode == "corrupt-cache") Assert(requests.SequenceEqual(new long[] { 0 }), "Good cached chunk redownloaded");
        }
        else Assert(error is IOException or InvalidDataException && !File.Exists(path), "Invalid response committed");
    });

    foreach (bool badHash in new[] { false, true })
    await Test("full install/fromFolder, bad manifest=" + badHash, async () =>
    {
        string dir = Fresh(), manifest = Manifest("file.bin", data, "override");
        string config = V1(Pack("manifest.json", manifest));
        if (badHash) config = config.Replace(Hash(Encoding.UTF8.GetBytes(manifest)), new string('0', 32));
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/manifest.json" => Json(manifest),
            "/override/file.bin" => Bytes(data),
            _ => throw new Exception("Wrong resource route: " + request.RequestUri)
        }));
        var error = await Error(() => Run(Installer(Manager(client, dir), client), GameInstallerKind.Install));
        Assert(badHash ? error != null && !File.Exists(Path.Combine(dir, "file.bin")) : error == null && File.ReadAllBytes(Path.Combine(dir, "file.bin")).SequenceEqual(data), "Install/hash result incorrect: " + error);
    });

    await Test("explicit group paths, alternate list, chunk and routing metadata", async () =>
    {
        string json = """{"resource":[],"groupResource":[{"dest":"arbitrary.krpdiff","size":"12","fromFolder":"override","chunkInfos":[{"start":"0","end":"11","md5":"abc"}]}],"applyTypes":["group"],"groupInfos":[{"dest":"arbitrary.krpdiff","srcFiles":[],"dstFiles":[{"dest":"file.bin","chunkInfos":[{"start":0,"end":11,"md5":"abc"}]}]}]}""";
        using var client = new HttpClient(new Handler(_ => Json(json)));
        object installer = Installer(Manager(client, Fresh()), client);
        var parsed = (await (Task<WuwaApiResponsePatchIndex?>)Call(installer, installerType, "DownloadPatchIndexAsync", "https://example.test/patch.json", CancellationToken.None)!)!;
        Assert(parsed.Resource.Single().FromFolder == "override" && parsed.Resource[0].ChunkInfos?.Length == 1 && parsed.GroupInfos[0].DstFiles[0].ChunkInfos?.Length == 1, "Parsed metadata missing");
        string dir = Fresh(); File.WriteAllBytes(Path.Combine(dir, "arbitrary.krpdiff"), data);
        Assert((string?)Call(null, patchType, "FindKrpdiffFile", dir, "file.bin", parsed.Resource, 0, 1, parsed.GroupInfos[0].Dest) == Path.Combine(dir, "arbitrary.krpdiff"), "Explicit mapping ignored");
    });

    foreach (bool large in new[] { false, true })
    await Test("preload repairs only corrupt cached file, large=" + large, async () =>
    {
        byte[] payload = large ? new byte[51 * 1024 * 1024] : data;
        string dir = Fresh(), temp = Path.Combine(dir, "TempPath", "TempPatchFiles");
        Directory.CreateDirectory(temp);
        File.WriteAllBytes(Path.Combine(temp, "bad.krpdiff"), payload);
        using (var stream = File.OpenWrite(Path.Combine(temp, "bad.krpdiff"))) stream.WriteByte(255);
        File.WriteAllBytes(Path.Combine(temp, "good.krpdiff"), data);
        string patch = "{\"resource\":[" + JsonSerializer.Serialize(new { dest = "bad.krpdiff", size = payload.Length, md5 = Hash(payload) }) + "," + JsonSerializer.Serialize(new { dest = "good.krpdiff", size = data.Length, md5 = Hash(data) }) + "]}";
        string config = V1(Pack("future.json", Manifest("file.bin", data), patch: Pack("patch.json", patch, "patch", "3.6.1")), true);
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/current.json" => Json(Manifest("file.bin", data)),
            "/patch.json" => Json(patch),
            "/patch/bad.krpdiff" => Count(),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Count() { Interlocked.Increment(ref downloads); return Bytes(payload); }
        object installer = Installer(Manager(client, dir), client);
        await Run(installer, GameInstallerKind.Preload);
        await Run(installer, GameInstallerKind.Preload);
        Assert(downloads == 1 && File.Exists(Path.Combine(temp, ".version")) && (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "app-game-config.json")))!["version"] == "3.6.1", "Preload cache/version behavior incorrect");
    });

    foreach (string bundleName in new[] { "default", "HD", "SD", "UHD" })
    foreach (bool preload in new[] { false, true })
    await Test("V2 two-pack " + bundleName + " " + (preload ? "preload then update reuses cache" : "update and version records"), async () =>
    {
        string dir = Fresh(), first = Manifest("base.bin", data, "override"), second = Manifest("audio.bin", data);
        string config = V2(Pack("base.json", first), Pack("audio.json", second, "audio"), preload);
        config = config.Replace("\"default\":", "\"" + bundleName + "\":");
        if (bundleName is "SD" or "UHD")
        {
            var alternatives = JsonNode.Parse(config)!;
            alternatives["bundles"]!["HD"] = new JsonObject { ["resourcePacks"] = new JsonArray("unselected") };
            if (preload) alternatives["predownload"]!["bundles"]!["HD"] = alternatives["bundles"]!["HD"]!.DeepClone();
            config = alternatives.ToJsonString();
        }
        var installed = JsonNode.Parse("""{"custom":42,"bundles":{"other":{"version":"1.0.0","state":"repairing","resourcePacks":["shared"]}}}""")!;
        if (bundleName is "SD" or "UHD") installed["bundles"]![bundleName] = new JsonObject { ["version"] = "3.6.1" };
        File.WriteAllText(Path.Combine(dir, "launcherDownloadConfig.json"), installed.ToJsonString());
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/base.json" => Json(first), "/audio.json" => Json(second),
            "/override/base.bin" or "/audio/audio.bin" => Count(),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Count() { Interlocked.Increment(ref downloads); return Bytes(data); }
        object manager = Manager(client, dir), installer = Installer(manager, client);
        if (preload)
        {
            await Run(installer, GameInstallerKind.Preload);
            Assert(!File.Exists(Path.Combine(dir, "base.bin")) && !Directory.Exists(Path.Combine(dir, "launcherDownloadConfig")), "Preload committed files or versions");
        }
        await Run(installer, GameInstallerKind.Update);
        Assert(downloads == 2, "Unexpected payload downloads: " + downloads);
        foreach (string name in new[] { "base", "audio" })
            Assert((string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig", name + ".json")))!["version"] == "3.7.0", "Pack version missing");
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig.json")))!;
        Assert((int?)root["custom"] == 42 && (string?)root["bundles"]!["other"]!["state"] == "repairing" && root["bundles"]![bundleName]!["resourcePacks"]!.AsArray().Count == 2, "Bundle metadata lost");
        Assert(bundleName == "default" || root["bundles"]!["default"] == null, "Created an unrelated default bundle");
        var local = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "LocalGameResources.json")))!;
        Assert(local["resource"]!.AsArray().Count == 2, "Pack manifests not merged");
        await Run(installer, GameInstallerKind.Update);
        Assert(downloads == 2, "Completed packs redownloaded");
    });

    foreach (string mode in new[] { "complete", "stale-pack", "missing-pack", "incomplete-bundle", "downgrade" })
    foreach (bool locateAfterInit in new[] { false, true })
    await Test("official UHD update sync " + mode + ", locate after init=" + locateAfterInit, async () =>
    {
        string dir = Fresh(), manifest = Manifest("payload.bin", data);
        string config = V2(Pack("current.json", manifest), Pack("current.json", manifest))
            .Replace("\"default\":", "\"UHD\":");
        var apiConfig = JsonNode.Parse(config)!;
        apiConfig["bundles"]!["HD"] = new JsonObject { ["resourcePacks"] = new JsonArray("hd") };
        apiConfig["resourcePacks"]!["hd"] = JsonNode.Parse(Pack("hd.json", manifest));
        config = apiConfig.ToJsonString();
        foreach (string file in new[] { "Client/Binaries/Win64/Client-Win64-ShippingBase.dll",
                     "Client/Binaries/Win64/Client-Win64-Shipping.exe",
                     "Client/Binaries/Win64/ThirdParty/KrPcSdk_Global/KRSDKRes/KRSDK.bin", "app-game-config.json" })
        {
            string path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.EndsWith(".json") ? "{\"version\":\"3.6.1\"}" : "installed");
        }
        var installed = JsonNode.Parse("""{"version":"3.6.1","bundles":{"UHD":{"version":"3.7.0","state":"","resourcePacks":["base","audio"]}}}""")!;
        if (mode == "incomplete-bundle") installed["bundles"]!["UHD"]!["state"] = "repairing";
        File.WriteAllText(Path.Combine(dir, "launcherDownloadConfig.json"), installed.ToJsonString());
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/index.json")
            ? Json(config) : throw new Exception("Unexpected request: " + request.RequestUri)));
        object manager = Manager(client, dir);
        foreach (string pack in new[] { "base", "audio" })
        {
            if (mode == "missing-pack" && pack == "audio") continue;
            Call(manager, managerType, "WritePackVersion", pack,
                new GameVersion(mode == "stale-pack" && pack == "audio" ? "3.6.1" : "3.7.0"));
        }
        if (mode == "downgrade")
        {
            Set(manager, "DEBUG_AllowDowngrade", true);
            File.WriteAllText(Path.Combine(dir, "app-game-config.json"), """{"version":"3.6.1","DEBUG_allowDowngrade":true}""");
        }
        if (locateAfterInit) ((IGameManager)manager).SetGamePath("");
        await Invoke(manager, "InitAsyncInner", true, CancellationToken.None);
        if (locateAfterInit)
        {
            ((IGameManager)manager).SetGamePath(dir);
            Call(manager, managerType, "LoadConfig");
        }
        ((IGameManager)manager).IsGameHasUpdate(out bool update);
        Assert(update == (mode != "complete"), "Incorrect update state after official UHD update");
        string expectedVersion = mode == "complete" ? "3.7.0" : "3.6.1";
        Assert((string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "app-game-config.json")))!["version"] == expectedVersion,
            "Incorrect persisted version after official UHD update");
    });

    await Test("official endpoint exposes preload while current bundle needs update", async () =>
    {
        string dir = Fresh(), manifest = Manifest("payload.bin", data);
        var config = JsonNode.Parse(V2(Pack("current.json", manifest, version: "3.7.2"),
            Pack("current.json", manifest, version: "3.7.2"), true))!;
        foreach (var section in new[] { config, config["predownload"]! })
        {
            section["bundles"]!["HD"] = section["bundles"]!["default"]!.DeepClone();
            section["bundles"]!.AsObject().Remove("default");
        }
        foreach (var pack in config["predownload"]!["resourcePacks"]!.AsObject())
        {
            pack.Value!["version"] = "3.8.0";
            pack.Value["indexFile"] = "preload.json";
        }
        foreach (string file in new[] { "Client/Binaries/Win64/Client-Win64-ShippingBase.dll",
                     "Client/Binaries/Win64/Client-Win64-Shipping.exe",
                     "Client/Binaries/Win64/ThirdParty/KrPcSdk_Global/KRSDKRes/KRSDK.bin", "app-game-config.json" })
        {
            string path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.EndsWith(".json") ? "{\"version\":\"3.7.1\"}" : "installed");
        }
        string installed = """{"version":"3.7.1","bundles":{"HD":{"version":"3.7.1","state":"","resourcePacks":["base","audio"]}}}""";
        File.WriteAllText(Path.Combine(dir, "launcherDownloadConfig.json"), installed);
        const string endpoint = "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/50004_P7xcUZnEr1AXIGON25E6KjpOgTlVrg6e/G153/official/index.json";
        var preset = new Hi3Helper.Plugin.Wuwa.Management.PresetConfig.WuwaGlobalPresetConfig();
        Assert((string?)managerType.GetProperty("GameConfigUrl", flags)!.GetValue(preset.GameManager) == endpoint, "Global preset uses the legacy endpoint");
        var steam = new Hi3Helper.Plugin.Wuwa.Management.PresetConfig.WuwaSteamPresetConfig();
        Assert((string?)managerType.GetProperty("GameConfigUrl", flags)!.GetValue(steam.GameManager) == endpoint, "Steam preset uses the legacy endpoint");
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            endpoint => Json(config.ToJsonString()),
            "https://example.test/preload.json" => Json(manifest),
            "https://example.test/base/payload.bin" => Download(),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Download() { downloads++; return Bytes(data); }
        object manager = Manager(client, dir, "3.7.1");
        Set(manager, "GameConfigUrl", endpoint);
        await Invoke(manager, "InitAsyncInner", true, CancellationToken.None);
        ((IGameManager)manager).IsGameHasUpdate(out bool update);
        ((IGameManager)manager).IsGameHasPreload(out bool preload);
        ((IGameManager)manager).GetApiPreloadGameVersion(out var version);
        Assert(update && preload && version == new GameVersion("3.8.0"), "Upcoming preload hidden by current update");
        await Run(Installer(manager, client), GameInstallerKind.Preload);
        Assert(downloads == 2 && !File.Exists(Path.Combine(dir, "payload.bin")), "Preload did not cache both packs without installing");
        Assert(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig.json")) == installed, "Preload changed installed bundle metadata");
    });

    foreach (string mode in new[] { "verified", "verified-direct", "corrupt", "missing", "bad-manifest", "missing-hash", "cancelled", "SD" })
    await Test("legacy pack reconciliation: " + mode, async () =>
    {
        string dir = Fresh(), bundleName = mode == "SD" ? "SD" : "HD";
        byte[] commonPatch = [1, 2, 3], hdPatch = [4, 5, 6, 7];
        string fullCommon = Manifest("common.bin", data), fullHd = Manifest("hd.bin", data);
        var legacy = JsonNode.Parse(fullCommon)!;
        legacy["resource"]!.AsArray().Add(JsonNode.Parse(fullHd)!["resource"]![0]!.DeepClone());
        if (mode == "missing-hash") legacy["resource"]![1]!.AsObject().Remove("md5");
        string sourceManifest = legacy.ToJsonString();
        const string sourceUrl = "https://example.test/legacy/resource/50004/3.7.1/indexFile.json";
        var stored = new JsonObject
        {
            ["version"] = "3.7.1", ["resourceIndexUrl"] = sourceUrl, ["resourceBaseUrl"] = "legacy/zip",
            ["resourceIndexMd5"] = mode == "bad-manifest" ? new string('0', 32) : Hash(Encoding.UTF8.GetBytes(sourceManifest)),
            ["DEBUG_skipPreflight"] = true
        };
        File.WriteAllText(Path.Combine(dir, "app-game-config.json"), stored.ToJsonString());
        File.WriteAllBytes(Path.Combine(dir, "common.bin"), data);
        if (mode != "missing") File.WriteAllBytes(Path.Combine(dir, "hd.bin"), mode == "corrupt" ? new byte[data.Length] : data);
        string installed = "{\"custom\":42,\"version\":\"3.7.1\",\"bundles\":{\"" + bundleName + "\":{\"version\":\"3.7.0\",\"state\":\"\",\"resourcePacks\":[\"common\",\"hd\"]},\"other\":{\"version\":\"1.0.0\"}}}";
        File.WriteAllText(Path.Combine(dir, "launcherDownloadConfig.json"), installed);
        var config = JsonNode.Parse(V2(Pack("common-full.json", fullCommon, "payload", "3.8.0",
            Pack("common-old.json", fullCommon, "payload", "3.7.0")),
            Pack("hd-full.json", fullHd, "payload", "3.8.0", Pack("hd-old.json", fullHd, "payload", "3.7.0")), true)
            .Replace("\"default\":", "\"" + bundleName + "\":")
            .Replace("\"base\"", "\"common\"").Replace("\"audio\"", "\"hd\""))!;
        string commonManifest = Manifest("common.krpdiff", commonPatch), hdManifest = Manifest("hd.krpdiff", hdPatch);
        config["predownload"]!["resourcePacks"]!["common"]!["patchConfig"]!.AsArray().Add(JsonNode.Parse(Pack("common-patch.json", commonManifest, "patch", "3.7.1")));
        config["predownload"]!["resourcePacks"]!["hd"]!["patchConfig"]!.AsArray().Add(JsonNode.Parse(Pack("hd-patch.json", hdManifest, "patch", "3.7.1")));
        foreach (var pack in config["resourcePacks"]!.AsObject()) pack.Value!["version"] = "3.7.2";
        int sourceReads = 0, downloads = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config.ToJsonString()),
            "/legacy/resource/50004/3.7.1/indexFile.json" => Source(),
            "/common-full.json" or "/common-old.json" => Json(fullCommon),
            "/hd-full.json" or "/hd-old.json" => Json(fullHd),
            "/common-patch.json" => Json(commonManifest), "/hd-patch.json" => Json(hdManifest),
            "/patch/common.krpdiff" => Download(commonPatch), "/patch/hd.krpdiff" => Download(hdPatch),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Source() { sourceReads++; if (mode == "cancelled") cancellation.Cancel(); return Json(sourceManifest); }
        HttpResponseMessage Download(byte[] bytes) { downloads++; return Bytes(bytes); }
        object manager = Manager(client, dir, "3.7.1");
        Set(manager, "CurrentGameConfigNode", stored);
        Set(manager, "DEBUG_SkipPreflight", true);
        foreach (string pack in new[] { "common", "hd" }) Call(manager, managerType, "WritePackVersion", pack, new GameVersion("3.7.0"));
        object installer = Installer(manager, client);
        if (mode != "verified-direct")
        {
            long size = await (Task<long>)Call(installer, installerType, "GetGameSizeAsyncInner", GameInstallerKind.Preload, CancellationToken.None)!;
            long downloaded = await (Task<long>)Call(installer, installerType, "GetGameDownloadedSizeAsyncInner", GameInstallerKind.Preload, CancellationToken.None)!;
            Assert(size == (mode == "SD" ? data.Length * 2 : 7) && downloaded == 0, "Wrong preload estimate: " + size);
            Assert(sourceReads == 0 && File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig.json")) == installed,
                "Preload availability checked installed hashes or changed version records");
            foreach (string pack in new[] { "common", "hd" })
                Assert((GameVersion)Call(manager, managerType, "ReadPackVersion", pack)! == new GameVersion("3.7.0"), "Size query promoted a pack");
        }
        if (mode != "SD")
        {
            var error = await Error(() => Run(installer, GameInstallerKind.Preload, cancellation.Token));
            Assert(mode.StartsWith("verified") ? error == null : mode == "cancelled" ? error is OperationCanceledException : error is InvalidDataException,
                "Preload did not enforce source verification: " + error);
        }
        string expected = mode.StartsWith("verified") ? "3.7.1" : "3.7.0";
        foreach (string pack in new[] { "common", "hd" })
            Assert((GameVersion)Call(manager, managerType, "ReadPackVersion", pack)! == new GameVersion(expected), "Unverified pack version promoted");
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig.json")))!;
        Assert((string?)root["version"] == "3.7.1" && (string?)root["bundles"]![bundleName]!["version"] == expected &&
            (int?)root["custom"] == 42 && (string?)root["bundles"]!["other"]!["version"] == "1.0.0", "Bundle reconciliation lost metadata");
        if (mode.StartsWith("verified"))
        {
            await Run(installer, GameInstallerKind.Preload);
            long downloaded = await (Task<long>)Call(installer, installerType, "GetGameDownloadedSizeAsyncInner", GameInstallerKind.Preload, CancellationToken.None)!;
            Assert(downloads == 2 && downloaded == 7 && sourceReads == 1, "Preload did not reuse the reconciled source version");
            Assert(File.ReadAllBytes(Path.Combine(dir, "common.bin")).SequenceEqual(data) && File.ReadAllBytes(Path.Combine(dir, "hd.bin")).SequenceEqual(data), "Preload changed installed files");
            Assert(File.ReadAllText(Path.Combine(dir, "app-game-config.json")) == stored.ToJsonString(), "Reconciliation rewrote the main version");
        }
        else Assert(downloads == 0, "Verification downloaded a payload");
    });

    foreach (string change in new[] { "unchanged", "corrupt", "truncated", "deleted", "manifest", "partial" })
    await Test("preload status uses metadata: " + change, async () =>
    {
        string dir = Fresh();
        byte[] basePayload = data;
        string first = Manifest("base.bin", basePayload), second = Manifest("audio.bin", data);
        string config = V2(Pack("base.json", first), Pack("audio.json", second), true);
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/base.json" => Json(first), "/audio.json" => Json(second),
            "/base/base.bin" => Download(basePayload), "/base/audio.bin" => Download(data),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Download(byte[] bytes) { downloads++; return Bytes(bytes); }
        object FreshInstaller() => Installer(Manager(client, dir), client);
        async Task<long> Downloaded() => await (Task<long>)Call(FreshInstaller(), installerType,
            "GetGameDownloadedSizeAsyncInner", GameInstallerKind.Preload, CancellationToken.None)!;
        await Run(FreshInstaller(), GameInstallerKind.Preload);
        string file = Path.Combine(dir, "launcherDownload", "base", "3.7.0", "base.bin");
        string marker = Path.Combine(Path.GetDirectoryName(file)!, ".version");
        File.WriteAllText(marker, "3.7.0");
        // An exclusive read handle permits metadata checks but prevents a second
        // content read. A new installer also rules out an instance-only cache.
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert(await Downloaded() == data.Length * 2, "Status query read preload contents on a cold check");

        if (change == "corrupt")
        {
            File.WriteAllBytes(file, new byte[data.Length]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        }
        if (change == "truncated") File.WriteAllBytes(file, data[..^1]);
        if (change == "deleted") File.Delete(file);
        if (change == "manifest")
        {
            basePayload = data.Reverse().ToArray();
            first = Manifest("base.bin", basePayload);
            config = V2(Pack("base.json", first), Pack("audio.json", second), true);
        }
        if (change == "partial")
        {
            File.Delete(file);
            File.WriteAllBytes(file + ".tmp", data[..5]);
        }
        long expected = change is "truncated" or "deleted" ? data.Length : change == "partial" ? data.Length + 5 : data.Length * 2;
        Assert(await Downloaded() == expected, "Downloaded byte count did not match cached file metadata");
        if (change == "corrupt")
        {
            await Run(FreshInstaller(), GameInstallerKind.Update);
            Assert(downloads == 3 && File.ReadAllBytes(Path.Combine(dir, "base.bin")).SequenceEqual(basePayload),
                "Installation trusted the fast status check instead of verifying cached content");
            return;
        }
        await Run(FreshInstaller(), GameInstallerKind.Preload);
        Assert(downloads == (change == "unchanged" ? 2 : 3), "Preload repair discarded a good cached pack");
        Assert(await Downloaded() == data.Length * 2, "Repaired preload was not complete");
    });

    await Test("V2 second-pack failure preserves completed pack and bundle version", async () =>
    {
        string dir = Fresh(), first = Manifest("base.bin", data), second = Manifest("audio.bin", data);
        string config = V2(Pack("base.json", first), Pack("audio.json", second));
        bool fail = true; int baseDownloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/base.json" => Json(first), "/audio.json" => Json(second),
            "/base/base.bin" => First(),
            "/base/audio.bin" => fail ? Bytes(new byte[12]) : Bytes(data),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage First() { baseDownloads++; return Bytes(data); }
        object installer = Installer(Manager(client, dir), client);
        Assert(await Error(() => Run(installer, GameInstallerKind.Update)) != null, "Failure unexpectedly succeeded");
        Assert(File.Exists(Path.Combine(dir, "launcherDownloadConfig", "base.json")) && !File.Exists(Path.Combine(dir, "launcherDownloadConfig", "audio.json")) && !File.Exists(Path.Combine(dir, "app-game-config.json")), "Failed bundle promoted");
        fail = false;
        await Run(installer, GameInstallerKind.Update);
        Assert(baseDownloads == 1, "Completed pack downloaded again");
    });
    await Test("whole resume rejects wrong range and truncated response", async () =>
    {
        foreach (bool truncated in new[] { false, true })
        {
            string path = Path.Combine(Fresh(), "file.bin");
            File.WriteAllBytes(path + ".tmp", data[..3]);
            using var client = new HttpClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[3..6]) };
                response.Content.Headers.ContentRange = truncated ? new(3, 11, 12) : new(0, 2, 12);
                return response;
            }));
            object installer = Installer(Manager(client, Fresh()), client);
            Assert(await Error(() => Invoke(installer, "DownloadWholeFileAsync", new Uri("https://example.test/file"), path, CancellationToken.None, null)) is InvalidDataException && !File.Exists(path), "Invalid whole response committed");
        }
    });

    await Test("two decoder failures do not force a healthy third group to full download", async () =>
    {
        string dir = Fresh();
        byte[] old = Encoding.ASCII.GetBytes("old"), target = Encoding.ASCII.GetBytes("old!");
        byte[] validPatch = [..Encoding.ASCII.GetBytes("HDIFF13&zstd\0"), 4, 3, 1, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 3, 3, (byte)'!'];
        byte[] head = [..Encoding.ASCII.GetBytes("f2.bin\0f2.bin\0"), 0, 0, 3, 4, 0];
        validPatch = [..Encoding.ASCII.GetBytes("HDIFF19&zstd&fadler64\0"),
            1, 1, 1, 7, 1, 7, 1, 3, 1, 4, 0, 0, 0, 0, 0, 0, (byte)head.Length, 0, 0,
            ..head, ..validPatch];
        var files = Enumerable.Range(0, 3).Select(i => new { dest = $"f{i}.bin", size = target.Length, md5 = Hash(target) }).ToArray();
        var sources = files.Select(f => new { f.dest, size = old.Length, md5 = Hash(old) }).ToArray();
        string full = JsonSerializer.Serialize(new { resource = files });
        string prior = JsonSerializer.Serialize(new { resource = sources });
        byte[][] patches = [data, data, validPatch];
        string patch = JsonSerializer.Serialize(new
        {
            resource = patches.Select((bytes, i) => new { dest = $"arbitrary-{i}.krpdiff", size = bytes.Length, md5 = Hash(bytes) }),
            groupInfos = files.Select((f, i) => new { dest = $"arbitrary-{i}.krpdiff", srcFiles = new[] { sources[i] }, dstFiles = new[] { f } })
        });
        foreach (var source in sources) File.WriteAllBytes(Path.Combine(dir, source.dest), old);
        File.WriteAllText(Path.Combine(dir, "app-game-config.json"), """{"version":"3.6.1","resourceIndexUrl":"https://example.test/old.json","resourceBaseUrl":"oldbase"}""");
        string config = V1(Pack("full.json", full, patch: Pack("patch.json", patch, "patch", "3.6.1")));
        var downloads = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json")) return Json(config);
            if (path == "/full.json") return Json(full);
            if (path == "/old.json") return Json(prior);
            if (path == "/patch.json") return Json(patch);
            if (path.StartsWith("/patch/arbitrary-")) return Bytes(patches[int.Parse(Path.GetFileName(path).Split('-')[1].Split('.')[0])]);
            if (path.StartsWith("/base/f")) { downloads.Add(path); return Bytes(target); }
            throw new Exception("Unexpected request: " + request.RequestUri);
        }));
        object manager = Manager(client, dir);
        Set(manager, "CurrentGameConfigNode", JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "app-game-config.json")))!.AsObject());
        await Run(Installer(manager, client), GameInstallerKind.Update);
        Assert(downloads.SequenceEqual(new[] { "/base/f0.bin", "/base/f1.bin" }), "Healthy third group was downloaded in full");
        Assert(File.ReadAllBytes(Path.Combine(dir, "f2.bin")).SequenceEqual(target), "Healthy third group was not patched");
    });

    await Test("V2 pack CDN/folder routing, secondary pack version and repair", async () =>
    {
        string dir = Fresh(), first = Manifest("base.bin", data), second = Manifest("audio.bin", data);
        string firstPack = Pack("base.json", first), secondPack = Pack("audio.json", second, "audio");
        firstPack = firstPack[..^1] + ",\"folder\":\"files\"}";
        string config = V2(firstPack, secondPack);
        config = config[..^1] + ",\"cdnList\":[{\"url\":\"https://cdn.test/\"}]}";
        int downloads = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/index.json")) return Json(config);
            Assert(request.RequestUri.Host == "cdn.test", "Pack CDN ignored");
            return request.RequestUri.AbsolutePath switch
            {
                "/base.json" => Json(first), "/audio.json" => Json(second),
                "/base/files/base.bin" or "/audio/audio.bin" => Count(),
                _ => throw new Exception("Wrong pack folder: " + request.RequestUri)
            };
        }));
        HttpResponseMessage Count() { downloads++; return Bytes(data); }
        object manager = Manager(client, dir), installer = Installer(manager, client);
        await Run(installer, GameInstallerKind.Update);
        File.WriteAllBytes(Path.Combine(dir, "audio.bin"), new byte[12]);
        await Run(installer, GameInstallerKind.Update);
        Assert(downloads == 3 && File.ReadAllBytes(Path.Combine(dir, "audio.bin")).SequenceEqual(data), "Same-version repair redownloaded good pack or failed");
        string installedPath = Path.Combine(dir, "launcherDownloadConfig.json");
        var installed = JsonNode.Parse(File.ReadAllText(installedPath))!;
        installed["bundles"]!["default"]!["state"] = "repairing";
        File.WriteAllText(installedPath, installed.ToJsonString());
        Assert((bool)managerType.GetProperty("HasResourcePackUpdate", flags)!.GetValue(manager)!, "Incomplete bundle state ignored");
        await Run(installer, GameInstallerKind.Update);
        Assert(!(bool)managerType.GetProperty("HasResourcePackUpdate", flags)!.GetValue(manager)! && downloads == 3, "Bundle repair state not completed safely");
        Call(manager, managerType, "WritePackVersion", "audio", new GameVersion("3.6.1"));
        Assert((bool)managerType.GetProperty("HasResourcePackUpdate", flags)!.GetValue(manager)!, "Secondary pack update missed");
    });

    await Test("V2 patch preload/update retains another pack's file", async () =>
    {
        string dir = Fresh();
        string first = Manifest("base.bin", data), second = Manifest("audio.bin", data);
        var origin = JsonNode.Parse(first)!;
        origin["resource"]!.AsArray().Add(JsonNode.Parse(Manifest("extra.bin", data))!["resource"]![0]!.DeepClone());
        first = origin.ToJsonString();
        string patch = JsonSerializer.Serialize(new
        {
            resource = new[] { new { dest = "base.bin", size = data.Length, md5 = Hash(data), fromFolder = "override" } },
            deleteFiles = new[] { "audio.bin" }
        });
        string config = V2(Pack("base.json", first, patch: Pack("patch.json", patch, "patch", "3.6.1")), Pack("audio.json", second), true);
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/base.json" => Json(first), "/audio.json" => Json(second), "/patch.json" => Json(patch),
            "/override/base.bin" or "/base/extra.bin" => Count(),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        HttpResponseMessage Count() { downloads++; return Bytes(data); }
        File.WriteAllBytes(Path.Combine(dir, "audio.bin"), data);
        object manager = Manager(client, dir);
        Call(manager, managerType, "WritePackVersion", "base", new GameVersion("3.6.1"));
        Call(manager, managerType, "WritePackVersion", "audio", new GameVersion("3.7.0"));
        object installer = Installer(manager, client);
        await Run(installer, GameInstallerKind.Preload);
        Assert((string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig", "base.json")))!["version"] == "3.6.1", "Patch preload promoted pack");
        await Run(installer, GameInstallerKind.Update);
        Assert(downloads == 2 && File.Exists(Path.Combine(dir, "extra.bin")) && File.ReadAllBytes(Path.Combine(dir, "base.bin")).SequenceEqual(data) && File.Exists(Path.Combine(dir, "audio.bin")), "Patch cache not reused or another pack file deleted");
    });
    foreach (string mode in new[] { "full", "patch", "corrupt" })
    await Test("pack completion verification progress: " + mode, async () =>
    {
        string dir = Fresh(), manifest = Manifest("payload.bin", data);
        string pack = Pack("full.json", manifest, patch: mode == "patch" ? Pack("patch.json", manifest, "base", "3.6.1") : null);
        string config = "{\"bundles\":{\"default\":{\"resourcePacks\":[\"base\"]}},\"resourcePacks\":{\"base\":" + pack + "}}";
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/full.json" or "/patch.json" => Json(manifest),
            "/base/payload.bin" => Bytes(mode == "corrupt" ? new byte[data.Length] : data),
            _ => throw new Exception("Unexpected request: " + request.RequestUri)
        }));
        object manager = Manager(client, dir), installer = Installer(manager, client);
        Call(manager, managerType, "WritePackVersion", "base", new GameVersion("3.6.1"));
        var snapshots = new List<(InstallProgressState State, InstallProgress Progress)>();
        InstallProgressState state = InstallProgressState.Idle;
        bool installed = false, verifiedAfterInstall = false, completed = false;
        void State(InstallProgressState next)
        {
            state = next;
            if (next == InstallProgressState.Install) installed = true;
            if (installed && next == InstallProgressState.Verify) verifiedAfterInstall = true;
            if (next == InstallProgressState.Completed) completed = true;
        }
        void Progress(in InstallProgress progress) => snapshots.Add((state, progress));
        var error = await Error(() => Invoke(installer, "StartPatchCoreAsync", GameInstallerKind.Update, false,
            (InstallProgressDelegate)Progress, (InstallProgressStateDelegate)State, CancellationToken.None));
        if (mode == "corrupt")
        {
            Assert(error != null && !completed && !File.Exists(Path.Combine(dir, "payload.bin")), "Corrupt pack completed");
            Assert((string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "launcherDownloadConfig", "base.json")))!["version"] == "3.6.1", "Corrupt pack version promoted");
            return;
        }
        Assert(error == null && completed && File.ReadAllBytes(Path.Combine(dir, "payload.bin")).SequenceEqual(data), "Pack failed: " + error);
        if (mode == "full") Assert(!verifiedAfterInstall, "Full install repeated complete-pack verification after committing");
        else
        {
            var final = snapshots.TakeLast(2).ToArray();
            Assert(final.Length == 2 && final.All(p => p.State == InstallProgressState.Verify) &&
                final[0].Progress.DownloadedBytes == 0 && final[0].Progress.StateCount == 0 &&
                final[1].Progress.DownloadedBytes == data.Length && final[1].Progress.StateCount == 1,
                "Final pack verification did not reset and advance progress");
        }
    });

    await Test("source repair verifies staging before replacing installed bytes", async () =>
    {
        string dir = Fresh(), file = Path.Combine(dir, "source.bin");
        byte[] original = [9, 8, 7];
        File.WriteAllBytes(file, original);
        using var client = new HttpClient(new Handler(_ => Bytes(new byte[12])));
        object installer = Installer(Manager(client, dir), client);
        var sourceType = assembly.GetType("Hi3Helper.Plugin.Wuwa.Management.WuwaSourceReconciliation", true)!;
        var error = await Error(async () => await (Task)Call(null, sourceType, "DownloadCanonicalFileAsync",
            installer, "https://example.test", "https://example.test/old", "source.bin", null,
            12UL, Hash(data), dir, null, null, CancellationToken.None)!);
        Assert(error is InvalidDataException && File.ReadAllBytes(file).SequenceEqual(original), "Bad source repair overwrote installed bytes");
    });

    await Test("wrong patch manifest hash cannot complete preload", async () =>
    {
        string dir = Fresh(), full = Manifest("file.bin", data), patch = Manifest("file.krpdiff", data);
        string patchConfig = Pack("patch.json", patch, "patch", "3.6.1")
            .Replace(Hash(Encoding.UTF8.GetBytes(patch)), new string('0', 32));
        string config = V1(Pack("future.json", full, patch: patchConfig), true);
        int downloads = 0;
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/index.json") => Json(config),
            "/current.json" => Json(full), "/patch.json" => Json(patch),
            _ => Count()
        }));
        HttpResponseMessage Count() { downloads++; return Bytes(data); }
        var error = await Error(() => Run(Installer(Manager(client, dir), client), GameInstallerKind.Preload));
        Assert(error is InvalidDataException && downloads == 0 && !File.Exists(Path.Combine(dir, "TempPath", "TempPatchFiles", ".version")), "Untrusted patch manifest was used");
    });
    Console.WriteLine($"{passed} compatibility checks passed.");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
}
