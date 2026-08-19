using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.Wuwa.Management.Settings;

internal sealed partial class WuwaLocalStorage : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadWrite = 0x00000002;
    private static readonly nint SqliteTransient = new(-1);
    private nint _database;

    internal WuwaLocalStorage(string path)
    {
        int result = SqliteOpen(path, out _database, OpenReadWrite, null);
        if (result != SqliteOk)
        {
            string message = GetError();
            Dispose();
            throw new IOException($"Could not open Wuthering Waves settings database: {message}");
        }

        SqliteBusyTimeout(_database, 3000);
    }

    internal string? Read(string key)
    {
        const string sql = "SELECT value FROM LocalStorage WHERE key = ?1";
        nint statement = Prepare(sql);
        try
        {
            Bind(statement, key);
            int result = SqliteStep(statement);
            if (result == SqliteDone) return null;
            if (result != SqliteRow) ThrowDatabaseError();
            nint value = SqliteColumnText(statement, 0);
            return value == nint.Zero ? null : Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            SqliteFinalize(statement);
        }
    }

    internal void Write(string key, string value)
    {
        const string updateSql = "UPDATE LocalStorage SET value = ?2 WHERE key = ?1";
        nint statement = Prepare(updateSql);
        try
        {
            Bind(statement, key, 1);
            Bind(statement, value, 2);
            StepToCompletion(statement);
        }
        finally
        {
            SqliteFinalize(statement);
        }

        if (SqliteChanges(_database) != 0) return;

        const string insertSql = "INSERT INTO LocalStorage(key, value) VALUES (?1, ?2)";
        statement = Prepare(insertSql);
        try
        {
            Bind(statement, key, 1);
            Bind(statement, value, 2);
            StepToCompletion(statement);
        }
        finally
        {
            SqliteFinalize(statement);
        }
    }

    public void Dispose()
    {
        if (_database == nint.Zero) return;
        SqliteClose(_database);
        _database = nint.Zero;
    }

    private nint Prepare(string sql)
    {
        int result = SqlitePrepare(_database, sql, -1, out nint statement, nint.Zero);
        if (result != SqliteOk) ThrowDatabaseError();
        return statement;
    }

    private void Bind(nint statement, string value, int index = 1)
    {
        if (SqliteBindText(statement, index, value, -1, SqliteTransient) != SqliteOk) ThrowDatabaseError();
    }

    private void StepToCompletion(nint statement)
    {
        if (SqliteStep(statement) != SqliteDone) ThrowDatabaseError();
    }

    private void ThrowDatabaseError() => throw new IOException($"Could not update Wuthering Waves settings: {GetError()}");

    private string GetError() => _database == nint.Zero
        ? "Unknown SQLite error"
        : Marshal.PtrToStringUTF8(SqliteErrorMessage(_database)) ?? "Unknown SQLite error";

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SqliteOpen(string fileName, out nint database, int flags, string? virtualFileSystem);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static partial int SqliteClose(nint database);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout")]
    private static partial int SqliteBusyTimeout(nint database, int milliseconds);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SqlitePrepare(nint database, string sql, int byteCount, out nint statement, nint tail);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SqliteBindText(nint statement, int index, string value, int byteCount, nint destructor);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static partial int SqliteStep(nint statement);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static partial nint SqliteColumnText(nint statement, int column);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static partial int SqliteFinalize(nint statement);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_changes")]
    private static partial int SqliteChanges(nint database);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg")]
    private static partial nint SqliteErrorMessage(nint database);
}
