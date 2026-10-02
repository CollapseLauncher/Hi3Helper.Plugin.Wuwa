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

        // Tier-specific package names identify the installed resources. Multiple
        // tiers can coexist, so only a single identified tier is unambiguous.
        string? tier = null;
        foreach (string candidate in new[] { "HD", "SD", "UHD" })
        {
            string directory = Path.Combine(gamePath, "Client", "Content", candidate);
            if (!Directory.Exists(directory)
                || !Directory.EnumerateFiles(directory, $"pakchunk*-{candidate}-WindowsNoEditor.pak", SearchOption.TopDirectoryOnly).Any())
                continue;

            if (tier != null)
                throw new InvalidOperationException("Multiple Wuthering Waves resource tiers were found. Select an installed tier using -krqlv=sd, -krqlv=hd, or -krqlv=uhd in custom launch arguments.");

            tier = candidate.ToLowerInvariant();
        }

        if (tier == null)
            throw new InvalidOperationException("The installed Wuthering Waves resource tier could not be identified. Verify the installed resources or specify their tier using -krqlv=sd, -krqlv=hd, or -krqlv=uhd in custom launch arguments.");

        string tierArgument = "-krqlv=" + tier;
        return string.IsNullOrWhiteSpace(arguments) ? tierArgument : tierArgument + " " + arguments;
    }
}
