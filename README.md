# SaveVault

A local-first Windows backup and restore application for personal files, discovered/custom game saves, and music. Built with **C# / .NET 10 and WPF**, with a platform-independent backup and discovery engine.

## First milestone

- Create, edit, and persist profiles with multiple source folders and exclusion patterns.
- Categorize profiles as personal files, game saves, or music.
- Back up to a local folder, external drive, or an accessible network share.
- Keep versioned snapshots with whole-file content deduplication across profiles in the same repository.
- Verify SHA-256 checksums before committing a snapshot; verify existing snapshots on demand.
- Browse backup history and select individual files for restore.
- Preview target paths and conflicts before restoring to a separate folder.
- Skip existing files by default, or overwrite with preserved rollback copies.
- Reopen a repository without the original profiles or source folders.
- Cancel operations, track activity, and inspect daily diagnostic logs.
- Preserve last-write timestamps and empty folders when restoring a complete snapshot.
- Discover game-library folders and Steam installations, including additional Steam libraries.
- Resolve known saves/configuration using a locally cached Ludusavi catalog, aliases, and manual title overrides.
- Search alternate layouts and recognized save-path settings; perform a bounded deeper search or watch changes while playing.
- Select individual files or source-relative file/folder patterns without backing up unrelated parent-folder contents.

**Current backup format is unencrypted and uncompressed.** Encryption, scheduling, registry export, additional launcher adapters, and Android support are subsequent milestones in [docs/ROADMAP.md](docs/ROADMAP.md).

## Run on Windows

For development, install the **.NET 10 SDK** and use Windows 10/11:

```powershell
dotnet restore SaveVault.sln
dotnet build SaveVault.sln --configuration Release
dotnet test tests/SaveVault.Core.Tests/SaveVault.Core.Tests.csproj --configuration Release
dotnet run --project src/SaveVault.App/SaveVault.App.csproj
```

Or open `SaveVault.sln` in a Visual Studio version supporting .NET 10 with the .NET desktop development workload.

### Publish a portable Windows application

```powershell
dotnet publish src/SaveVault.App/SaveVault.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/windows-x64
```

Copy the **entire** `artifacts/windows-x64` folder to Windows and launch `SaveVault.exe`. This self-contained build includes the runtime. The application is currently unsigned.

## First backup

1. Click **New profile**, choose a name and category.
2. Add source folders. For a game, add its save directory (for example, `%APPDATA%\StardewValley\Saves` through the folder picker).
3. Choose a separate backup destination on your external drive or network share.
4. Optionally add exclusion patterns, one per line. `*.tmp` matches file names; `cache` excludes a folder named `cache` and its contents. Patterns containing `/` match source-relative paths. Patterns use `*` and `?`; this is not gitignore-style globbing.
5. Click **Save profile**, then **Back up saved profile**. Backup uses the saved settings.
6. In **History & restore**, choose a snapshot and click **Verify snapshot**.

### Discover game saves

1. Open **Games**. Your initial library folder is `G:\Games`; add other libraries one per line if needed.
2. Click **Update full catalog** once for broad coverage. Seven starter titles are included offline: Cyberpunk 2077, Elden Ring, Alan Wake II, Undertale, Dying Light: The Beast, Gothic 1 Remake, and Age of Empires II: HD Edition. Successful updates are cached locally; failed/cancelled updates preserve the previous cache.
3. Click **Discover games**. Steam metadata is read automatically; custom libraries list immediate subfolders. These may include tools/mods as well as games. Use **Add game folder** for nested installations, or **Select executable** (it is not launched).
4. Review the catalog match. Renamed installations may need a manual search/selection—for example, choose **Age of Empires II: HD Edition** for `Age Of Empires II HD`. Canonical title overrides and manually added installations are retained when a discovery/watch operation saves settings.
5. Click **Find save locations**. The app checks Windows catalog rules, account-ID variations, known ID-based alternate layouts, and recognized `SavePath`/`SaveDir` settings in bounded installation-local INI scans.
6. Review confidence, content tags, evidence, file selections, size, and modification time. Separate account/save locations remain separate candidates. Nothing is selected automatically. Folder rules include the whole folder, and overlapping catalog save/config rules can include the same files.
7. Check the candidates you want, then **Create / update profile draft from selected**. In **Profile**, review the destination and click **Save profile**, then back up normally. Repeated confirmations for the same installation merge into the current draft, retaining unsaved sources, source IDs, name, destination, and exclusions. Manually added installations remain remembered even when a library/Steam scan also finds them.

**Search more locations** inspects the likely data roots, custom watch/search roots, and installation folder. Heuristics require game/install-path evidence plus save-like files; a `.sav` extension alone does not select an unrelated folder. Individual heuristic matches select files, not entire AppData roots.

For unknown locations, configure **watch/search folders**, click **Start watching**, launch the game yourself, make a manual save, exit, then **Finish & review changes**. Watch results are individual changed/new files. Watchers start before the baseline scan; finishing always performs another metadata scan, including after event overflow. Changes from other applications can appear and must be reviewed. **Discard watch** releases the session without using its results.

Scans are cancellable, skip linked paths, and report access errors, unsupported catalog tokens, and search limits. Deep search is bounded to 100,000 entries and six directory levels; watch metadata scans use ten levels and 100,000 entries, with at most 5,000 changed-file results. Pattern expansion also has limits. Narrow the roots and repeat if a limit is reported. Metadata rescanning cannot identify same-size/same-timestamp content changes if their watcher events were lost. No discovery mode guarantees every save or attributes filesystem events to a process. Registry rules are reported but are not backed up by folder profiles.

Windows Known Folder APIs resolve redirected Saved Games/LocalLow; standard system folders resolve Documents/AppData/Public Documents. Profiles keep Windows paths. In WSL, `G:\Games` is `/mnt/g/Games` for read-only engine validation; do not replace Windows profile paths with `/mnt/...`. The WPF interface still runs on Windows.

### Precise source selection

**Add file** selects just that file inside its parent folder. Discovered filename patterns such as `*.sav` also include new matching slots on later backups. `SourceFolder.Includes` is empty for a whole folder; otherwise it contains source-relative selections such as `savegame`, `config/settings.ini`, or `*.sav`. `*` and `?` match within one component; selecting a folder includes its subtree. Recursive `**` includes and escaping paths are rejected. Profile exclusions still take precedence. Old profiles without `Includes` retain whole-folder behavior; restore still uses the snapshot's actual file listing.

## Restore

1. Select a profile, or click **Open repository…** and select the original backup destination (the parent of `SaveVaultRepository`). This works when the source folders no longer exist.
2. Select a snapshot, then select the files to recover. All files are selected initially.
3. Choose a separate restore folder. Direct restore into original source folders is intentionally outside this first milestone.
4. Choose conflict behavior. The default skips existing files. The overwrite option preserves previous files in a `.savevault-rollback-<id>` folder under the restore destination.
5. Click **Preview restore…**, review the paths, and choose **Verify and restore**.

Restored files are grouped under `<source-label>-<source-id>` folders so similarly named sources cannot collide. Complete-snapshot restore includes empty folders; selective file restore creates only the folders those files need.

If restore is cancelled partway through, completed files remain. Re-run with **skip existing files** to recover the remaining files. Interrupted backups publish no snapshot; re-running can reuse completed, verified objects. This is restart/reuse behavior, not byte-level transfer resume.

## Storage and reliability

```text
<backup destination>/SaveVaultRepository/
├── .operation.lock
├── snapshots/<snapshot-id>.json
├── objects/<first-two-hash-characters>/<sha256>
└── work/<temporary-files>
```

- Each snapshot contains the complete file listing for that point in time; only new unique content gets a permanent object. All source files are read and staged one at a time on every run in this version.
- Backup objects are flushed to disk and rehashed before the manifest is atomically committed.
- Objects and snapshots are not automatically deleted. Files deleted from a source remain recoverable from older snapshots.
- Unreferenced objects may remain after interruption. Automatic cleanup/retention requires a future reachability-aware implementation.
- An exclusive repository lock prevents simultaneous SaveVault backup/verify/restore operations against that repository.
- Missing sources, access errors, and detectable mid-copy changes fail a backup rather than publishing an incomplete snapshot.
- Links and junctions within sources are skipped and recorded. Linked source roots, backup destinations, and restore paths are rejected.
- Restore validates manifest paths, verifies all selected objects before writing, then hashes each staged copy before replacing its target.
- No VSS or coordinated application snapshot is implemented yet. Close games and music-library applications before backing up their live data. Files can change between individual reads; this is not a transaction-consistent snapshot of a running application.
- This version preserves bytes and last-write timestamps, not Windows ACLs, alternate data streams, hard links, or application-specific exports. Music metadata embedded in file contents is preserved; external library databases must be selected separately.
- SHA-256 detects content corruption. Manifests are not authenticated or encrypted yet.

Profile settings live in `%LOCALAPPDATA%\SaveVault\profiles.json`; daily logs live in `%LOCALAPPDATA%\SaveVault\logs`. JSON persistence keeps the first milestone dependency-light; a SQLite search/index catalog can be added later without changing snapshot semantics.

Game discovery settings/overrides live in `game-discovery.json`, and the normalized catalog cache in `game-catalog.json` beside the profiles. Catalog updates use [Ludusavi Manifest](https://github.com/mtkennerly/ludusavi-manifest), whose location data is compiled from PCGamingWiki/Steam. YAML parsing uses YamlDotNet. Required notices are included in `THIRD-PARTY-NOTICES.txt` and copied into the portable build.

## Development on Linux

The core and headless view-model regression tests run on Linux. The test project links the production view models with supplied discovery environments and fake dialogs; it does not load WPF or Windows services. WPF can be cross-built using `EnableWindowsTargeting`, but its interface can only be run on Windows:

```bash
dotnet test tests/SaveVault.Core.Tests/SaveVault.Core.Tests.csproj --configuration Release
dotnet build SaveVault.sln --configuration Release
```

The initial development environment has a local SDK at `/home/shreef/.dotnet/dotnet` and lacks ICU. For that environment only, prefix commands with:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_SYSTEM_GLOBALIZATION_PREDEFINEDCULTURESONLY=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 /home/shreef/.dotnet/dotnet test tests/SaveVault.Core.Tests/SaveVault.Core.Tests.csproj --configuration Release
```

The Windows app itself does not enable invariant globalization.

If the invariant development environment reports `NETSDK1188` for test-package translations, use the command-line-only `-p:SatelliteResourceLanguages=en` while building/testing. This does not change the app's runtime globalization configuration.

## Structure

```text
src/SaveVault.Core/          Profiles, manifests, backup/restore, verification, path validation
src/SaveVault.App/           WPF views, MVVM state, folder/restore dialogs, activity logging
tests/SaveVault.Core.Tests/  Filesystem and headless view-model regressions in isolated temp folders
docs/                       Architecture and development roadmap
```

The GitHub Actions workflow runs core tests on Linux and Windows, cross-component build checks on Windows, and a Windows desktop-startup smoke check before uploading the portable build.
