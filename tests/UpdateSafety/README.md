# Update safety regression checks

Run `dotnet run --project tests/UpdateSafety/UpdateSafety.csproj`.

Uses the real discovery and file-integrity code with fake HTTP responses and temporary files.
Covers maintenance package/resource-root differences, ambiguous roots, Launcher group coverage,
HTTP and metadata failures, missing downloads, invalid replacement files, and cancellation.
Does not modify an installed game or contact the CDN.
