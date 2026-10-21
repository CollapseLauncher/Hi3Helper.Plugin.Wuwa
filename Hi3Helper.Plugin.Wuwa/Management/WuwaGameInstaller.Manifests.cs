using Hi3Helper.Plugin.Wuwa.Management.Api;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal partial class WuwaGameInstaller
{
    private WuwaApiResponseGameConfigRef? TargetConfig =>
        (GameManager as WuwaGameManager)?.ApiConfigReference;

    internal string BuildResourceUrl(WuwaApiResponseGameConfigRef? config, WuwaApiResponseResourceEntry entry)
    {
        string basis = entry.FromFolder ?? $"{config?.BaseUrl ?? GameResourceBasisPath}/{config?.Folder}";
        string root = Uri.TryCreate(basis, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? basis.TrimEnd('/') : $"{ApiResponseAssetUrl?.TrimEnd('/')}/{basis.Trim('/')}";
        return $"{root.TrimEnd('/')}/{EncodePathSegments(entry.Dest ?? "")}";
    }

    private async Task<byte[]> ReadManifestAsync(HttpResponseMessage response, string url, CancellationToken token)
    {
        // HttpClient has already decoded content encoding. Hash these entity bytes,
        // before parsing or rewriting JSON.
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        string? expected = (GameManager as WuwaGameManager)?.GetManifestMd5(url);
        if (!string.IsNullOrEmpty(expected) &&
            !Convert.ToHexString(MD5.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest MD5 mismatch: {url}");
        return bytes;
    }
}
