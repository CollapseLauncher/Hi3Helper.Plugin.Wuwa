Run from the repository root:

```powershell
dotnet run --project tests/DownloadProgress/DownloadProgress.csproj -c Release -p:Platform=x64
```

This standalone regression harness compiles the production download helpers and uses a fake HTTP handler. It tests whole-file and chunked retries after partial writes, pre-existing partial files, completed chunks, and servers that restart whole-file downloads instead of honoring resume requests. It checks output contents and verifies that progress never exceeds the file size. No network connection or game installation is needed to execute the tests after restore.
