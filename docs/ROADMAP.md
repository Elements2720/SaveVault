# Development roadmap

## Milestone 1 — Local folder and custom game-save foundation

Implemented:

- WPF desktop interface and profile editor.
- Multiple source folders, categories, exclusions, local/external/network destinations.
- Versioned, whole-file-deduplicated snapshots and integrity verification.
- History browsing and portable repository reopening.
- Selective alternate-folder restore with preview, skip conflicts, and rollback copies.
- Cancellation, operation logging, and filesystem integration tests.

Validation: Linux engine tests and Windows-targeted compilation can run in the initial environment. Native Windows UI and Windows filesystem behavior need Windows testing. CI includes Windows build/tests and a startup smoke check.

## Milestone 2 — Complete the local-backup MVP

1. Test desktop workflows on Windows: source/destination picking, removable-drive disconnects, permissions, locked saves, UNC paths, long paths, restore conflicts, and backup/recovery after reinstall.
2. Add authenticated encryption, a reviewed password derivation scheme, recovery-key export, and protected local credential storage. Version the format and test wrong passwords, modified ciphertext, and lost-key recovery flows.
3. Add a headless runner and Windows Task Scheduler integration, plus missed-schedule and destination-reconnection handling.
4. Add retention settings and reachability-tested object cleanup; cancelled cleanup must preserve retained snapshots.
5. Add disk-space estimates, richer scan progress, bounded retry rules, and notifications.
6. Add stronger resumable transfer support and an incremental-scan index while retaining optional full hashing.
7. Add optional compression, repository storage reporting, and large-history indexing.
8. Implement original-location restore with explicit conflict resolution and durable rollback; distinguish this from alternate-folder recovery.

## Milestone 3 — Game integrations

Implemented ahead of Android:

- Steam installation discovery, additional Steam libraries, and custom/manual installation roots (initial default `G:\Games`).
- Offline starter/full updateable Ludusavi catalog, aliases, installation names, game IDs, and persisted per-installation title overrides.
- Known save/config paths, alternate ID layouts, recognized save-path settings, bounded deeper search, and watch-while-playing candidates.
- Evidence/confidence, separate account locations, user-confirmed profile drafts, and precise file/folder-pattern sources that retain existing confirmations.

Remaining:

- Dedicated Epic/GOG/other launcher metadata adapters and additional catalog tokens/layouts.
- Registry export/restore for registry-backed games; current scans report these rules without backing them up.
- Process-attributed file tracing and richer scan indexing, beyond metadata/session correlation.
- Named/commented save snapshots and profile templates.
- Mod-list and game-configuration backup selection.
- Cloud-save conflict guidance and running-game detection.
- Emulator save templates.

## Milestone 4 — Android user files

- Windows Portable Devices/MTP discovery and transfer adapter.
- Device profiles for DCIM, Pictures, Movies, Music, Download, and Documents.
- Per-file transfer verification, disconnect/reconnect handling, and resumable jobs.
- Explicit, device-specific scope of accessible data.
- Optional ADB transport with pairing/setup instructions.

Modern Android does not permit unrestricted backup of arbitrary apps' private data. The application must expose actual available capabilities rather than promise a complete device image.

## Milestone 5 — Android companion app

- Paired, encrypted USB/local-network transfers.
- Supported contacts/calendar/document exports with platform permissions.
- App-specific export integrations and supported cross-device restore.
- Capability reporting by Android version and device.

## Milestone 6 — Cloud and advanced storage

- Encrypted repository/object-store abstraction.
- One initial cloud destination (for example, S3-compatible storage).
- Retries, credentials, quotas, and partial-upload recovery.
- Immutable retention where supported.
- Backup health reporting and optional anomaly detection.
- Additional providers and multi-device catalogs.
