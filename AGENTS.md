# SaveVault agent notes

## Build and verification

- Use .NET 10; `global.json` permits later 10.0 SDK feature bands. WPF targets `net10.0-windows` with `EnableWindowsTargeting`: Linux can compile it, but cannot run the desktop UI.
- Run from the repository root:

```sh
dotnet build SaveVault.sln --configuration Release
dotnet test tests/SaveVault.Core.Tests/SaveVault.Core.Tests.csproj --configuration Release
dotnet test tests/SaveVault.Core.Tests/SaveVault.Core.Tests.csproj --configuration Release --filter "FullyQualifiedName~CorruptionIsDetectedBeforeAnyRestoreFileIsWritten"
dotnet run --project src/SaveVault.App/SaveVault.App.csproj
dotnet publish src/SaveVault.App/SaveVault.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/windows-x64
```

- The `run` command requires Windows. Publish produces `SaveVault.exe`; distribute the entire output folder, not just the executable.
- In WSL, use `/home/shreef/.dotnet/dotnet` with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_SYSTEM_GLOBALIZATION_PREDEFINEDCULTURESONLY=0`; add command-line `-p:SatelliteResourceLanguages=en` for test-package locale warnings. Keep workarounds out of app runtime configuration. `G:\Games` is mounted at `/mnt/g/Games`; profiles must retain Windows paths.
- `Directory.Build.props` treats warnings as errors, including xUnit analyzer diagnostics.
- `.github/workflows/build.yml` runs Linux core tests and Windows build → Release tests with `--no-build` → publish → window-startup smoke check. Use `--no-build` only after a matching build; compilation alone does not verify WPF runtime behavior.
- Tests use GUID-isolated temporary folders cleaned in `Dispose`; headless regressions link production view models with supplied environments/fake dialogs. Link tests return early on Windows, so passing Windows tests do not establish link/junction coverage.

## Wiring and state

- `App.xaml.cs` supplies services/Windows discovery environment to `MainViewModel`; Loaded initializes profiles/Games. Reuse matching installation drafts; retain unsaved editor fields/sources and manual membership independently of store metadata. Games shares `RunAsync`; dispose its watch session on close. No DI/MVVM framework is used.
- Keep `SaveVault.Core` independent of WPF/Windows. `BackupRepository` owns validation and backup/restore semantics; `IUserDialogs` isolates desktop dialogs.
- Create `Progress<OperationProgress>` on the UI thread **before** `Task.Run`; creating it inside the worker loses UI-thread marshalling. Keep operation/cancellation handling in `MainViewModel.RunAsync`.
- Backup uses the saved `SelectedProfile`, not unsaved editor fields. Verification/restore use the currently browsed repository, which may differ from the selected profile's destination.
- Profiles/logs live under `%LOCALAPPDATA%\SaveVault`; snapshots are portable JSON manifests on the backup drive. Repository reopening must work without profile settings or existing source folders. Persistence is JSON, not SQLite.

## Backup/restore invariants

- `BackupRepository` takes the **parent destination** and appends `SaveVaultRepository`. Objects are shared across profiles, addressed by lowercase SHA-256; each manifest is a complete listing, not an incremental chain. Every source file is currently read/staged each run.
- Commit manifests last via `JsonStorage`'s flushed temporary-file/atomic-move path, only after object verification. Preserve the exclusive `.operation.lock` for backup/verify/restore. Failed/cancelled backups must not publish partial snapshots.
- Unreferenced objects can legitimately remain after interruption. Cleanup must account for every retained manifest across profiles, not just the latest snapshot or current profile.
- Restore rederives targets; directory allowlists are metadata-only so unselected conflicts do not block selective restore. Check selected targets/created directories, verify selected objects before writes, and hash staged copies. Default skip conflicts; keep rollback copies before overwrite. Restore is atomic per file.
- Restore is alternate-folder-only; source/destination overlap and linked roots are rejected. Source links are skipped and recorded. Keep path validation in the core, including Windows reserved names and case-insensitive manifest collision checks even on Linux.
- `PlanRestore(..., selectedFiles: null)` includes empty folders; an explicit file selection creates only required parents. Restore folders include source GUIDs to prevent same-named sources colliding.
- Exclusions use `FileSystemName.MatchesSimpleExpression`: names or source-relative paths, with matched directories pruned. `SourceFolder.Includes` selects relative files/folders with component-local `*`/`?`; empty means whole folder. Validate includes in core; merge confirmed selections without overlapping roots or discarding existing sources/IDs.

See `docs/ARCHITECTURE.md` and `docs/ROADMAP.md`. Format v1 is raw, unencrypted, uncompressed; encryption, scheduling, registry export, and Android are future work. Game scans use a bounded offline/updateable catalog and metadata watch rescans; retain incomplete-scan notices and do not claim universal detection/process attribution.
