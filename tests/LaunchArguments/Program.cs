using Hi3Helper.Plugin.Wuwa.Utils;

string root = Path.Combine(Path.GetTempPath(), "wuwa-launch-arguments-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    Check("hd-default", null, "-krqlv=hd", "HD");
    Check("hd-whitespace", "  ", "-krqlv=hd", "HD");
    Check("hd-arguments", "-dx11", "-krqlv=hd -dx11", "HD");
    Check("sd-only", null, "-krqlv=sd", "SD");
    Check("uhd-only", "-dx12", "-krqlv=uhd -dx12", "UHD");
    Check("hd-only", null, "-krqlv=hd", "HD");
    CheckAmbiguous("multiple", "SD", "UHD", "HD");
    CheckAmbiguous("without-hd", "SD", "UHD");
    CheckAmbiguous("unknown");
    Check("explicit", "-krqlv=uhd -dx12", "-krqlv=uhd -dx12", "HD");
    Check("explicit-case", "-KRQLV=SD", "-KRQLV=SD", "HD");

    string emptyHd = Path.Combine(root, "empty-hd", "Client", "Content", "HD");
    Directory.CreateDirectory(emptyHd);
    File.WriteAllText(Path.Combine(emptyHd, "pakchunk1-HD-WindowsNoEditor.sig"), "signature");
    Check("empty-hd", null, "-krqlv=sd", "SD");
    Directory.CreateDirectory(Path.Combine(root, "unrelated-pak", "Client", "Content", "HD"));
    File.WriteAllText(Path.Combine(root, "unrelated-pak", "Client", "Content", "HD", "unrelated.pak"), "package");
    Check("unrelated-pak", null, "-krqlv=sd", "SD");
    Check("explicit-multiple", "-krqlv=sd", "-krqlv=sd", "SD", "HD");
    Check("explicit-unknown", "-krqlv=uhd", "-krqlv=uhd");
    Console.WriteLine("All launch argument checks passed.");
}
finally
{
    Directory.Delete(root, true);
}

void CheckAmbiguous(string name, params string[] tiers)
{
    string gamePath = Path.Combine(root, name);
    foreach (string tier in tiers)
    {
        string directory = Path.Combine(gamePath, "Client", "Content", tier);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"pakchunk1-{tier}-WindowsNoEditor.pak"), "package");
    }

    try
    {
        WuwaLaunchArguments.AddResourceTier(gamePath, null);
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new Exception($"{name}: expected an unidentified or ambiguous tier error.");
}

void Check(string name, string? arguments, string expected, params string[] tiers)
{
    string gamePath = Path.Combine(root, name);
    foreach (string tier in tiers)
    {
        string directory = Path.Combine(gamePath, "Client", "Content", tier);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"pakchunk1-{tier}-WindowsNoEditor.pak"), "package");
    }

    string actual = WuwaLaunchArguments.AddResourceTier(gamePath, arguments);
    if (actual != expected)
        throw new Exception($"{name}: expected '{expected}', got '{actual}'.");
}
