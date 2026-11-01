<!-- Modified for EmberKit; based on ktxiaok/FireAxe 0.7.3. -->
# EmberKit

**[简体中文](README.zh-Hans.md)** · English

A personal Windows fork of **[ktxiaok / FireAxe](https://github.com/ktxiaok/FireAxe)**, with a few additions to make installing Left 4 Dead 2 Workshop mods from a shared TXT list easier for new players.

Thank you to **ktxiaok**, the original FireAxe author, for developing and open-sourcing the mod manager. Its existing downloader, groups, tags, priorities, references and conflict detection provide the foundation for this fork. Thanks also to every mod creator.

## Additions in this fork

- **TXT import and export:** share Workshop IDs, titles, enabled states and priorities; validate the game and available download before queueing items. A TXT list contains references, not mod files; installation requires network access.
- **Download visibility:** retain the existing progress/speed display and add persistent results with the final state, archived path, error details and an “open file location” action. Results can be removed; failed, uninstalled placeholders can also be deleted during gameplay, while installed mod files are retained. The mod list, search and detail panel reuse saved titles while keeping Workshop IDs visible. Failed, uninstalled items can be retried without duplicate records.
- **Classification by replaced content:** inspect VPK resource paths for weapons, characters and other categories; keep empty slots visible, such as “Pistol 0”, and flag multiple replacements of the same slot.
- **Reuse known directories:** infer and share the game directory across settings, imports and local-storage operations.
- **A short-name local store:** consolidate bodies into `addons/modebackup/m000001.vpk` and similar unique ASCII names. `mod-index.txt` maps the short ID to its SHA-256, display name, original title, Workshop ID, paths and enabled state. Only byte-identical bodies are deduplicated.
- **Synchronised game entries:** editable switches show enabled/disabled choices independently of pending deployment and parent-group restrictions. Enabled mods always receive short relative `.vpk` symbolic links at the top of `addons`, pointing to `modebackup` without copying content. Existing hard links are converted during sync; there is no link-type setting or automatic hard-link fallback on failure. Changes wait while the game is running until an explicit main-menu reload or game exit. Windows `.lnk` shortcuts are not game-loading entries.
- **Clipboard links use the shared downloader:** accepting a detected Workshop link opens Link Downloads with the URL filled in. Review it and click Add to download queue; progress and results use the same workflow as manual links and TXT imports.
- **A hash option:** link downloads, clipboard links and TXT imports skip the Workshop SHA-1 comparison by default. Clear the option to enable strict comparison; file-size and VPK-format checks remain, SHA-256 storage identity is still computed, and the result records the choice.
- **Experimental main-menu reload:** launch through the assistant once, then use one button to disconnect to the menu, unload addons, apply entries, remount and verify the game's VPK load order. Uninstalled downloads are skipped without blocking other mods. No manual console commands are needed. Some cached resources may still require a full restart; visual effects need in-game testing.
- **Portable application state:** settings, logs, game connection credentials and crash-report temporary files stay in `data` beside `EmberKit.exe`, independently of the working directory. The manager no longer uses Documents or AppData for these files. Mod-library metadata, download history and backups stay with `modebackup`.

Downloads are staged in `firefox_temp`, then validated and archived. Opening the manager, checking, or rescanning does not automatically download Workshop items. The detailed current workflow is in the [Chinese guide](docs/usage.zh-Hans.md).

## Build and check

Requires the .NET 10 SDK. Windows x64 is the target of this fork's added storage workflow.

```powershell
dotnet restore FireAxe.Checks/FireAxe.Checks.csproj
dotnet run --project FireAxe.Checks/FireAxe.Checks.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/package-windows.ps1
```

The checks create their own temporary VPKs, simulated game directories, a simulated console and headless UI previews; the normal check command does not operate on an installed game. The current suite has **191 checks**. Game effects require a separate in-game test.

The package script produces a self-contained `publish/EmberKit-Windows-x64-Portable` folder and matching ZIP; fully extract the archive and run `EmberKit.exe`. Packages start with an empty `data` directory. Optional `-WorkshopListPath` includes a TXT list, and `-OutputDirectory` selects a fresh output folder. Existing output folders and ZIPs are rejected to avoid packaging personal state. Internal assemblies and namespaces retain the upstream FireAxe names for compatibility.

## License

[Apache License 2.0](LICENSE.txt)
