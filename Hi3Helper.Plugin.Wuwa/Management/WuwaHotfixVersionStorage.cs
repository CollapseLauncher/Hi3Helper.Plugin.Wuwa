using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal static class WuwaHotfixVersionStorage
{
    private const int SqliteOpenReadWrite = 0x00000002;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteOpenV2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out nint database,
        int flags,
        nint vfs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteClose(nint database);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteExec(
        nint database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        nint callback,
        nint argument,
        out nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SqliteFree(nint value);

    internal static void TryUpdate(
        string gamePath,
        string? launcherTargetVersion,
        string? resourceTargetVersion,
        string? launcherMount)
    {
        string libraryPath = Path.Combine(
            gamePath, "Client", "Binaries", "Win64", "ThirdParty", "KrPcSdk_Global", "sqlite3.dll");
        string databasePath = Path.Combine(
            gamePath, "Client", "Saved", "DeviceSaved", "DeviceStorage.db");
        if (!File.Exists(libraryPath) || !File.Exists(databasePath))
            return;

        nint library = 0;
        nint database = 0;
        try
        {
            library = NativeLibrary.Load(libraryPath);
            var open = GetDelegate<SqliteOpenV2>(library, "sqlite3_open_v2");
            var close = GetDelegate<SqliteClose>(library, "sqlite3_close");
            var exec = GetDelegate<SqliteExec>(library, "sqlite3_exec");
            var free = GetDelegate<SqliteFree>(library, "sqlite3_free");

            if (open(databasePath, out database, SqliteOpenReadWrite, 0) != 0)
                throw new InvalidOperationException("sqlite3_open_v2 failed.");

            string sql = "BEGIN IMMEDIATE;";
            if (launcherTargetVersion != null)
                sql += Upsert("Version_Launcher", JsonString(launcherTargetVersion));
            if (resourceTargetVersion != null)
                sql += Upsert("Version_Resource", JsonString(resourceTargetVersion));
            if (launcherMount != null)
            {
                int deleteMarker = launcherMount.IndexOf("::Del::", StringComparison.Ordinal);
                string mountValue = deleteMarker < 0
                    ? launcherMount
                    : launcherMount[..deleteMarker];
                sql += Upsert("__kr_blvr__", JsonString(mountValue));
            }
            sql += "COMMIT;";
            int result = exec(database, sql, 0, 0, out nint error);
            if (result != 0)
            {
                string message = error == 0 ? $"SQLite error {result}" : Marshal.PtrToStringUTF8(error) ?? "";
                if (error != 0)
                    free(error);
                throw new InvalidOperationException(message);
            }

            close(database);
            database = 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaHotfixVersionStorage] Could not update device version records; " +
                "the game will reconcile them on next launch: {Error}", ex.Message);
        }
        finally
        {
            if (database != 0 && library != 0)
            {
                try { GetDelegate<SqliteClose>(library, "sqlite3_close")(database); }
                catch { /* best-effort */ }
            }

            if (library != 0)
                NativeLibrary.Free(library);
        }
    }

    private static T GetDelegate<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static string Upsert(string key, string value) =>
        $"INSERT INTO LocalStorage(key,value) VALUES('{Sql(key)}','{Sql(value)}') " +
        "ON CONFLICT(key) DO UPDATE SET value=excluded.value;";

    private static string Sql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
