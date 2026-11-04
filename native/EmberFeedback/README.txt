EmberKit Kill Feedback - experimental Windows x86 client module

New code: Apache-2.0, see ../../LICENSE.txt.
Reference assets have separate terms: assets/PROVENANCE.txt.
No reference mod executable or DLL is used by this implementation.

Build from the repository root (Visual Studio 2022 v143 C++ x86 tools and
Windows 10 SDK, .NET 10 SDK):
  powershell -NoProfile -File scripts/build-feedback.ps1 -RunChecks
  dotnet build FireAxe.GUI/FireAxe.GUI.csproj -c Release

The GUI build copies Feedback/EmberFeedback.dll and EmberFeedback.Loader.exe,
both x86, even when the manager is x64. Native checks use OUR isolated fixture
host and fixture DLL; no installed game or Steam process is started or loaded.

The previous plugin_load version failed the user's tests. That route required
-insecure, blocked normal server access and never produced a native startup
log. It is removed. This version requests ordinary Steam startup, retaining
only the existing -netconport / -netconpassword arguments. It does not request
-insecure, -secure, plugin_load or change Steam's saved launch options.

After authenticated console/path readback, the manager identifies the newly
started game by exact executable path and start time. The source-built x86
helper verifies the PID/path, maps OUR DLL into that process using the Windows
loader, and calls EmberFeedback_Start with the launch session. The helper
resolves the target system-loader address from its actual owning module/RVA;
it does not assume cross-process addresses match. Only the DLL path/session
are written; no shellcode, disk game-file patch or security-control patch is
used. Duplicate loading and unrelated target paths are rejected.

Start validates the configuration/session/game build and queues initialization
to the game's paint thread. Receive tables, event listeners, WIC and XAudio2
are initialized there. DLL loading and owner-thread initialization have
separate receipts; the manager does not treat the former as proof of the latter.
The client module remains resident for the lifetime of that game process.

Implementation:
- Source's client IGameEventListener2 subscriptions: player_death,
  infected_death and witch_killed; no damage attribution heuristics.
- GetPlayerForUserID/GetLocalPlayer and the local player's m_iTeamNum establish
  ownership. Receive-table metadata supplies team and zombie-class offsets.
- Client infected_death can carry infected_id=0 even when the attacker is
  available. Local canonical common-death events still qualify in all-zombies
  mode; only available victim IDs participate in identity deduplication.
  Player/Witch deaths continue to require a victim identity. Named player
  classes (Smoker/Boomer/Hunter/Spitter/Jockey/Charger/Tank) and CTerrorPlayer
  are both classified, with infected-team verification.
- MatSystemTopPanel painting uses a reversible IPanel009 vtable entry change.
  Surface031 draws our textures and captions. PNGs are decoded with Windows
  WIC; a four-voice XAudio2 pool plays local WAV files.
- The manager pins left4dead2.exe and the required Source DLLs; the module
  also pins engine.dll, client.dll, vguimatsurface.dll and vgui2.dll
  because these exact x86 interfaces are required. See source_abi.h.
- A new server/map can replace the engine event list, so missing subscriptions
  are re-established from the paint callback, at most once per second.
- The native module does not write the game directory, patch game files on
  disk or change any anti-cheat/security option.

State and diagnostics:
- data/Feedback/settings.ini contains enabled, scope and a random launch session.
- EmberFeedback.Loader.log records actual helper completion or load failure.
- EmberFeedback.log records native initialization phases.
- session-<session>.json contains PID, phase, scope, registered event kinds,
  death/accepted-kill/HUD-call counts, paint callbacks and audio availability.
  It is replaced atomically, at most once per second outside phase changes.
- The GUI rejects stale sessions, another PID, errors and missing owner-thread
  receipts. Missing events, rejected ownership and missing drawing can now be
  distinguished instead of all being described as "no effect".

Diagnostic build trace-20261103-1:
- Each native log line includes local time, process/thread and launch session.
  DEBUG_BUILD/INTERFACE/RECEIVE_TABLES/SUBSCRIBE identify the loaded build,
  interface pointers, receive-property offsets and per-event subscriptions.
- DEATH samples record the raw attacker/user/entity IDs, resolved local and
  attacker indexes, local team, victim class/type and the exact decision.
  The first 64 decisions are logged, then at most one detailed sample per 2s.
  No player names, chat, server addresses or console passwords are collected.
- session JSON records ALL decisionCounts, lastDecision, lastEvent, renderState,
  screen size, property offsets and audio request/start counts. -1 in raw IDs
  or victim metadata means missing/not inspected (or an explicit -1 value).
  HEARTBEAT logs totals and drawing state every 10s; TEXTURE/FONT/AUDIO record
  graphics initialization and actual audio results when that path is reached.
- session-<session>-exceptions.log is opened before our callbacks start. Their
  SEH filters record access violations/illegal instructions/integer divides,
  instruction and access addresses, DLL base/RVA, operation and event context.
  The filters ALWAYS continue normal exception search: they do not swallow,
  recover from or fix a crash. An empty file means no matching exception was
  observed escaping these callbacks; Steam's existing dumps remain relevant.
- This change adds evidence. It does not change ownership/scope/dedup rules
  or claim that the latest no-effect/crash report has been fixed.
  SEH filter behavior reference:
  https://learn.microsoft.com/en-us/cpp/cpp/try-except-statement?view=msvc-170

Validation limits:
Native checks include an actual x86 DLL load/start/receipt in our own fixture,
with an explicit host-ready handshake and duplicate-load rejection. Managed
checks cover normal argument preservation, structured helper arguments,
session/PID readback, counters, failure phases and both UI scopes.
These checks do not prove live game/VAC/server compatibility. No-insecure
startup is a property of our command construction, not VAC approval.
Live event delivery, rendering and audio still require the user's game test.
When the engine omits an event or identity, the module cannot recreate it.
The GUI uses this client's status receipt, not the server-plugin list.

ABI references (the reference mod was not decompiled into this source):
https://github.com/alliedmodders/hl2sdk/tree/l4d2/public
https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/igameevents.h
Current Surface031/Panel009 slots were checked statically against the local
game binaries; the older public L4D2 SDK names Surface030. In particular,
Surface031 texture upload has FOUR stack arguments, not the older header's
six. The typed wrapper and a mock vtable check cover this calling convention.
The pinned VClient016 class-list getter uses slot 7, not the old public SDK's
slot 6. Slot 8 is HudVidInit and returns 1. The initial client build wrongly
called slot 8, causing the user's two 2026-11-03 crashes at EmberFeedback.dll
+0x16ED9 when reading ClientClass::name at address 0x9. The getter was verified
against the local interface registration, vtable and crash-time class list.
A mock checks the corrected entry and rejects both incorrect slots. Native
receipts now record receive_tables/images/audio/events initialization stages.
The 2026-11-04 crash at the diagnostic DLL+0x1CAA9 identified another stale SDK
assumption: IClientNetworkable::GetClientClass is slot 1 in this pinned game,
not slot 2. The actual Smoker object's vtable and its constant class getter
were recovered from the dump and binary. NetworkClass wraps the verified
entry; tests reject slot 2 and cover named infected classes. The operation
checkpoint pointer is volatile so optimized builds retain exception context.
Native checks also replay 40 numeric event samples captured by our diagnostic
build: 24 local common kills with unavailable victim IDs now qualify, while
8 teammate kills and 8 deaths with no attacker remain excluded. A separate
burst check verifies that zero-ID common deaths do not deduplicate each other.
That revision reports debugVersion=fix-20261104-1.

The next user test confirmed Hunter ownership/type recognition and successful
audio startup, then crashed during the following game UI paint. The texture
creation call was still using slot 33, which this Surface031 implements as
DeleteTextureByID(int). Passing true deleted the game's texture ID 1, and its
boolean return was misread as a new integer ID (1280, then 171638784 in the log).
The next game upload dereferenced a missing texture at vguimatsurface.dll+0x16ED7.
Creation now uses verified slot 34; deletion uses slot 33 only for our created
IDs. The existing four-argument upload remains slot 28. Native tests exercise
two independent create/upload/delete lifecycles and preserve a pre-existing
game texture 1. No success claim relies on Surface031's validity/delete stubs.
The first completed HUD draw now forces a status receipt and logs
HUD_FIRST_DRAW_COMPLETE. This revision reports debugVersion=fix-20261104-2.

The user confirmed that version works in-game. The previous presentation
revision, hud-20261104-1, halves the skull size (54px to 27px at 1080p).
Chains of 1-3 kills show that many skulls. At 4+, a single latest-kill skull
and x4/x5/... replace the row; the whole icon/counter pair stays centered.
The duplicate lower combo caption is hidden in this compact mode.

The current anim-20261104-1 revision removes multiplier/combo numbers. The
skull drops from above at about double size, shrinks on impact, emits an
expanding ring, then slides left into a three-skull row. Older icons retain
their current positions and smoothly move right; displaced icons fade out.
Settled height remains 27px at 1080p. Existing headshot assets, captions and
audio are retained. A new chain clears old animation state even when the
reset and next kill happen between paint callbacks.

feedback_animation.h contains the time-based motion and bounded display
state. acceptedKills supplies unique serials even for multiple common deaths
in the same tick. Native checks cover impact timing, ring expansion, smooth
insertion, simultaneous kills, queue order and reset; they export actual poses
and the generated ring for an offline preview. render-feedback-preview.ps1
renders these poses and invokes an existing ffmpeg executable to encode MP4.
The ring is generated locally as RGBA and uses a third owned texture through
the already verified Surface031 create/upload/draw/delete methods. No new
game interface slots are used for the animation.

To adapt a new game build, review the interfaces, update hashes in both
source_abi.h and FireAxe.Core/KillFeedback.cs, run native and managed checks,
then perform an explicit live game test. A checksum match alone does not
establish runtime correctness.
