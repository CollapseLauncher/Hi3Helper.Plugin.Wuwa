using System;
using System.IO;
using System.Linq;

namespace Hi3Helper.Plugin.Wuwa.Utils;

internal static class WuwaLaunchArguments
{
    internal static string AddResourceTier(string gamePath, string? arguments)
    {
        if (arguments?.Contains("-krqlv=", StringComparison.OrdinalIgnoreCase) == true)
            return arguments;

        // Tier-specific packages can coexist. Prefer the highest installed tier.
        string? tier = null;
        foreach (string candidate in new[] { "UHD", "HD", "SD" })
        {
            string directory = Path.Combine(gamePath, "Client", "Content", candidate);
            if (!Directory.Exists(directory)
                || !Directory.EnumerateFiles(directory, $"pakchunk*-{candidate}-WindowsNoEditor.pak", SearchOption.TopDirectoryOnly).Any())
                continue;

            tier = candidate.ToLowerInvariant();
            break;
        }

        if (tier == null)
            throw new InvalidOperationException("The installed Wuthering Waves resource tier could not be identified. Verify the installed resources or specify their tier using -krqlv=sd, -krqlv=hd, or -krqlv=uhd in custom launch arguments.");

        string tierArgument = "-krqlv=" + tier;
        return string.IsNullOrWhiteSpace(arguments) ? tierArgument : tierArgument + " " + arguments;
    }
}
