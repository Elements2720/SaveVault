# Architecture

## Boundaries

`SaveVault.Core` has no UI or Windows dependencies. It owns backup semantics and validates its inputs independently of the desktop application. `BackupRepository` operates on one destination. `ProfileStore` persists user configuration separately from repository data.

`SaveVault.App` uses WPF and a small MVVM layer without an external UI framework. `MainViewModel` owns editor state and asynchronous operations. `IUserDialogs` isolates Windows folder selection and restore confirmation. File scanning and backup work run on worker threads; `Progress<T>` is created on the UI thread and marshals notifications back there.

The first milestone uses JSON for settings and manifests, rather than introducing SQLite before catalog queries need it. Snapshot manifests remain portable even if local profile settings are lost. **Open repository** loads the destination's manifests directly.

## Backup transaction

1. Validate sources and reject overlapping roots and linked paths.
2. Take an exclusive filesystem lock for this repository.
3. Walk each source, pruning nonmatching source includes and profile exclusions and recording skipped links. Includes are additive profile metadata: empty lists preserve old whole-folder behavior; relative file/folder patterns never change the restore listing semantics.
4. Stream each file to a staging object while computing SHA-256.
5. Check size and last-write time for detectable mid-copy changes.
6. Move new content to its hash-addressed path, or reuse an existing object.
7. Rehash each distinct referenced object and validate its recorded size.
8. Flush and atomically move the completed manifest into `snapshots`.

Cancellation or failure before step 8 leaves no committed snapshot. Prior snapshots remain valid. Objects completed before failure may be reused on a subsequent run. There is no automatic object garbage collection yet.

## Restore transaction

1. Validate the snapshot's version, source IDs, paths, object hashes, and sizes.
2. Build a plan with source-specific destination folders and current conflicts.
3. Present the plan in the UI.
4. Rebuild targets in the engine, rechecking conflicts and path safety.
5. Acquire the repository lock and verify selected objects before changing destination data.
6. Copy each object to a temporary file beside its target; hash and flush the copy.
7. Skip an existing file, or preserve its previous content in a rollback folder before replacement.
8. Atomically move the individual staged file into its final location.

Restore is atomic per file, not across the whole selected set. Cancellation preserves completed files and rollback copies. Repeated skip-conflict restore can complete an interrupted operation. Destination paths are rechecked for links immediately before writes; the application does not claim to defend against a malicious process swapping filesystem entries concurrently.

Restore directory allowlists are derived from validated snapshot metadata without probing unselected targets. Conflict/link checks apply to selected file targets and the directories actually being created, including empty folders selected by a full restore. An unrelated unselected file/folder conflict cannot block a selective restore; caller-supplied directories must still belong to the snapshot.

## Format v1

A manifest records the format version, snapshot and profile GUIDs, creation time, source names/IDs/original paths, directories, file sizes, UTC last-write times, lowercase SHA-256 hashes, and skipped links. Each manifest fully describes one restore point. Objects are shared across profiles in a repository and contain raw, uncompressed file bytes.

Format version 1 is not an encrypted or authenticated container. A future encryption format must be versioned and include authenticated manifests and a defined key-recovery story. Encryption must be designed before adding cloud destinations.

## Game discovery

`SaveVault.Core.Games` separates discovery from backup transactions. `GameCatalogStore` normalizes the generic Ludusavi YAML manifest into an offline JSON cache; downloads are size-bounded and parsed/validated before atomic replacement. Unknown manifest fields are tolerated. Aliases, install-directory names, Steam IDs, Windows/store conditions, save/config tags, notes, and registry-rule notices are retained. A small embedded starter works without a network connection.

`GameLibraryDiscovery` reads Steam library/manifests and immediate custom-library subfolders. `GameSaveDiscovery` resolves catalog tokens against a host-supplied `DiscoveryEnvironment`, expands bounded patterns, checks alternate ID layouts/recognized INI keys, and optionally searches for file-level heuristic candidates. The Windows adapter supplies real Known Folders and Steam registry metadata; the core does not reinterpret Linux home directories as Windows user profiles.

Absolute catalog paths support `.`/`..` components during expansion. Parent components following wildcards are resolved only after finding concrete matching directories, and links are checked before discarding a path component. Snapshot paths and source-relative includes continue to reject parent traversal.

`SaveWatchSession` subscribes before taking its metadata baseline, records bounded event evidence, and always rescans on finish. Overflow/error notices remain visible; no process attribution is claimed. Changed/new files become reviewable candidates. Depth/entry limits and metadata-only recovery mean exhaustive coverage is not guaranteed. Sessions dispose watchers on cancellation/failure, discard, and application close.

`GameDiscoveryViewModel` uses `MainViewModel.RunAsync` for shared operation/cancellation state. Candidate checkboxes start unchecked. `GameProfileSources.Merge` turns confirmed candidates into nonoverlapping precise sources, retaining existing selections and same-root IDs. The UI creates/updates a draft associated with an installation path; saving still requires ordinary profile validation. Discovery never modifies game files, launches executables, silently replaces confirmed paths, or weakens backup/restore path checks.

Repeated confirmations reuse the current editor when its installation path matches, preserving unsaved selections and editor fields. Manual installation membership is tracked independently of `Store`/launcher metadata and persisted in `ManualInstallations`; rediscovery can enrich the displayed record without forgetting manual membership. `App.OnStartup` supplies the Windows environment to the view models. Regression tests link those production view models against fake dialogs and a supplied portable environment to exercise these state transitions on Linux.

## Next extension boundaries

- **Additional game adapters:** extend launcher/token/layout rules and registry export separately from backup transactions; preserve confidence/evidence and user-confirmed selections.
- **Scheduling:** a headless runner invokes the same engine and uses the same repository lock as the desktop app.
- **Destination providers:** introduce an object-store abstraction with immutable object writes and manifest commit semantics before implementing cloud APIs.
- **Android:** introduce an explicit source/transfer abstraction for MTP/ADB; Windows filesystem paths are not a sufficient abstraction for phones.
- **Retention:** compute objects reachable from retained manifests before removing any object. Keep cleanup separate from snapshot creation.
- **SQLite:** use an indexed, rebuildable catalog for search and large histories, preserving portable repository manifests.
