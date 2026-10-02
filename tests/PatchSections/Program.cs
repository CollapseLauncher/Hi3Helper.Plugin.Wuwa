using System.Text;
using Hi3Helper.Plugin.Wuwa.Utils;

// A compressed cover section followed by raw RLE controls must not be decoded
// as concatenated Zstd frames. Exercise both wrapper entry points with real patches.
string root = Path.Combine(Path.GetTempPath(), "wuwa-patch-sections-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    foreach (bool directory in new[] { false, true })
    foreach (bool compressed in new[] { true, false })
    {
        string source = Path.Combine(root, "source");
        string output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        string sourceFile = Path.Combine(source, "file");
        File.WriteAllText(sourceFile, "old");
        string patch = Path.Combine(root, "test.krpdiff");
        File.WriteAllBytes(patch, CreatePatch(directory, compressed));

        if (directory)
            HPatchZNative.ApplyDirPatch(source, patch, output);
        else
        {
            Directory.CreateDirectory(output);
            HPatchZNative.ApplyPatch(sourceFile, patch, Path.Combine(output, "file"));
        }

        if (File.ReadAllText(Path.Combine(output, "file")) != "old!")
            throw new Exception("Patched output does not match expected bytes.");
        Console.WriteLine($"PASS: directory={directory}, compressed cover={compressed}");
    }
}
finally
{
    Directory.Delete(root, true);
}

static byte[] CreatePatch(bool directory, bool compressed)
{
    using var patch = new MemoryStream();
    if (directory)
    {
        patch.Write(Encoding.ASCII.GetBytes("HDIFF19&zstd&fadler64\0"));
        // Kuro directory head: one path per side, reference index 0, old/new
        // sizes 3/4, and an unused output hash. No identical-file copies.
        byte[] head = [.. Encoding.ASCII.GetBytes("file\0file\0"), 0, 0, 3, 4, 0];
        patch.Write(new byte[] { 1, 1, 1, 5, 1, 5, 1, 3, 1, 4, 0, 0, 0, 0, 0, 0,
            (byte)head.Length, 0, 0 });
        patch.Write(head);
    }
    patch.Write(Encoding.ASCII.GetBytes("HDIFF13&zstd\0"));
    byte[] cover = [0, 0, 3]; // Copy three source bytes at offset zero.
    using var compressor = new ZstdSharp.Compressor();
    byte[] storedCover = compressed ? compressor.Wrap(cover).ToArray() : cover;
    patch.Write(new byte[] { 4, 3, 1, 3, (byte)(compressed ? storedCover.Length : 0),
        1, 0, 0, 0, 1, 0 });
    patch.Write(storedCover);
    patch.WriteByte(3); // Four zero RLE deltas, stored without compression.
    patch.WriteByte((byte)'!');
    return patch.ToArray();
}
