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
- **Numbered local storage:** physical VPKs use `0.vpk`, `1.vpk`, and so on in `addons/modebackup`. `mod-index.txt` maps IDs to readable names and full SHA-256 hashes for verified deduplication. Offline organization migrates legacy names and records their original paths. A dismissible startup notice recommends backing up any existing folder layout before organizing. [Storage guide](docs/readable-filenames.zh-Hans.md)
- **Manually applied game entries:** editable switches save enabled/disabled choices and stay pending both during and outside gameplay. Toggles, priorities, downloads and imports do not trigger full scans or entry writes; game exit and reopening the manager do not apply pending changes. Assistant startup goes straight to Steam using the last applied entries, while still preparing the connection and selected feedback/interpolation settings. Apply Mod commits pending changes together, using short relative `.vpk` symbolic links from `addons` to `modebackup`. Explicit organization and sync also synchronize entries. Existing hard links are converted during sync, without an automatic fallback. Windows `.lnk` shortcuts are not game-loading entries.
- **Clipboard links use the shared downloader:** accepting a detected Workshop link opens Link Downloads with the URL filled in. Review it and click Add to download queue; progress and results use the same workflow as manual links and TXT imports.
- **A hash option and retained downloads:** link downloads, clipboard links and TXT imports skip the Workshop SHA-1 comparison by default. Clear the option to enable strict comparison. A Steam metadata size mismatch is a warning; valid VPKs still install. Failed downloads retain available files with a location action in the results. VPK-format checks and SHA-256 storage identity remain.
- **One-click experimental main-menu reload:** the compact left tool pane launches through the assistant. Apply Mod on the main page immediately disconnects an active match, returns to the menu, unloads addons, applies entries, remounts and verifies the game's VPK load order. The button explains that it leaves the match without a second prompt. Offline, it synchronizes local entries. Some cached resources may still require a full restart.
- **Portable application state:** settings, logs, game connection credentials and crash-report temporary files stay in `data` beside `EmberKit.exe`, independently of the working directory. The manager no longer uses Documents or AppData for these files. Mod-library metadata, download history and backups stay with `modebackup`.
- **Optional animated kill feedback:** enable it beside the launch button and choose all zombies or special infected only. A local kill drops and shrinks a skull, emits a ring and slides into a three-icon queue, with headshot art and audio. Ordinary Steam launch arguments are retained; no `-insecure` option is added. Reference images and sounds have separate provenance and are not covered by the code's Apache-2.0 license.
- **Independent category and object folders:** AKs, pistols and infected can remain expanded together, preserving their states through toggles, tag changes, rescans and detail-page navigation. Each mod has one browse category and object, with persistent manual overrides. Inline rows show summaries, available thumbnails and enable switches; explicit list and detail buttons open the full page. Compact tools and the workspace scroll independently, with draggable dividers. All affected resources still participate in conflict checks.
- **Compact browsing:** wide workspaces arrange objects in two independent vertical lanes; narrow windows use one. Short mod rows show a title, useful description, author, thumbnail and switch. Repeated titles are omitted from descriptions, and a small tag button opens the editor without consuming a whole row.
- **Workshop collections:** links and TXT imports expand nested collections, deduplicate items and skip existing mods; explicit item settings take priority. Downloads install locally without changing Steam account subscriptions.
- **Optional pistol auto-clicking:** a saved setting under the small Additional Features entry is off by default. After launching through the assistant, holding the left button clicks repeatedly with pistols; release, menus, focus loss and weapon changes stop it. Weapon fire rates are unchanged.
- **Optional interpolation preset:** a saved checkbox adds `+cl_interp_ratio 1 +cl_interp 0.065` to the next assistant Steam launch. It defaults off, is shared by both launch entries and needs no native module by itself. Servers may constrain the final interpolation value.
- **Locate and adjust problems:** clicking a mod title in the floating problem tool expands its category and object, scrolls to it and highlights it while keeping both windows interactive. Campaign metadata identifies resource-only map parts. Shared map assets become compatibility advice; different contents at the same map or mission path, and non-map conflicts, still require attention. Full resource details remain available.
- **Local VPK import:** a visible tool-pane button and File-menu entry choose one or more local VPKs, retain external originals, validate and archive them, report failures and duplicates, and locate imported mods in their categories. Existing enable choices and tags are preserved for identical content. Import is available during gameplay; applying changes remains a separate explicit action.
- **Opt-in animation trimming:** select inline mod rows, then read their durations under Additional Features. Each batch row has independent reload and draw tail cuts in negative decimal seconds, with whole-frame previews and affected events. An optional checkbox imports and enables the edited version and disables the old one; old files and records are always retained. Delete is a separate inline action shared with the full list. Applying changes remains explicit. Verified MDL49/53 embedded layouts are supported; other layouts report their limitation. This edits client animation, not a server's firing timer.
- **Share reproducible edits:** edited VPKs and TXT v2 lists carry the original Workshop ID, source SHA-256 and ordered reload/draw cuts, including repeated edits and per-sequence adjustments. Import downloads the original once, retains it and generates each requested variant with its own switch and priority. Different source versions report a mismatch while retaining the original. Local-only sources cannot be reconstructed from a Workshop TXT reference. Only processed mods show a compact edit summary in the catalog.

Downloads are staged in `firefox_temp`, then validated and archived. Opening the manager, checking, or rescanning does not automatically download Workshop items. The detailed current workflow is in the [Chinese guide](docs/usage.zh-Hans.md).

## Build and check

Requires the .NET 10 SDK and Visual Studio 2022 C++ Build Tools (v143, Win32 and Windows SDK) for the feedback module. Windows x64 is the manager's target; the game module and loader are x86.

```powershell
dotnet restore FireAxe.Checks/FireAxe.Checks.csproj
dotnet run --project FireAxe.Checks/FireAxe.Checks.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/package-windows.ps1
```

The checks create their own temporary VPKs, simulated game directories, a simulated console and headless UI previews; the normal check command does not operate on an installed game. The current suite has **394 checks**, plus native animation, input-state, texture-lifecycle, attribution and isolated-loader checks via `scripts/build-feedback.ps1 -RunChecks`. Animation exports use the game's VPK 1 layout and verify every entry's CRC before publishing. Game effects require a separate in-game test.

The package script produces a self-contained `publish/EmberKit-Windows-x64-Portable` folder and matching ZIP; fully extract the archive and run `EmberKit.exe`. Packages start with an empty `data` directory. Optional `-WorkshopListPath` includes a TXT list, and `-OutputDirectory` selects a fresh output folder. Existing output folders and ZIPs are rejected to avoid packaging personal state. Internal assemblies and namespaces retain the upstream FireAxe names for compatibility.

## License

[Apache License 2.0](LICENSE.txt)
