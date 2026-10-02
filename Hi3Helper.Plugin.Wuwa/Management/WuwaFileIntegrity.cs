using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal static class WuwaFileIntegrity
{
    internal static async Task VerifyAsync(string path, ulong size, string? md5, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = File.OpenRead(path);
        if (size > 0 && (ulong)stream.Length != size)
            throw new InvalidDataException($"File size mismatch: {path}");
        if (!string.IsNullOrEmpty(md5))
        {
            string actual = Convert.ToHexString(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!actual.Equals(md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"File MD5 mismatch: {path}");
        }
        else if (size == 0)
        {
            throw new InvalidDataException($"No hash or size available to verify: {path}");
        }
    }

    internal static async Task CommitAsync(
        string stagedPath, string destination, ulong size, string? md5, CancellationToken token)
    {
        await VerifyAsync(stagedPath, size, md5, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        File.Move(stagedPath, destination, overwrite: true);
    }
}
