using Hi3Helper.Plugin.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.Wuwa;

public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports() => Load<WuwaPlugin>(!RuntimeFeature.IsDynamicCodeCompiled ? new Core.Management.GameVersion(0, 2, 1, 0) : default); // Loads the IPlugin instance as WuwaPlugin.

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* exportName, void** delegateP) =>
        TryGetApiExportPointer(exportName, delegateP);

}