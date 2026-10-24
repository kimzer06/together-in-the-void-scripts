# Together in the Void — Code Sample

A small, curated selection of the C# I wrote for **Together in the Void**,
a 2-player online co-op puzzle-platformer in the style of *It Takes Two*.
Lyra can freeze time, Bobby can open portals, and every puzzle needs both
of them.

I was the **sole programmer** (gameplay, netcode, UI, tools) on a
6-person team: 1 game designer, 2 2D artists, 1 3D artist and
1 animator/rigger. The full game loop is finished and runs on
**Photon Fusion 2 (Shared Mode)**.

▶ Playable build: https://hakiren.itch.io/together-in-the-void
▶ Portfolio: https://kimzer06.github.io

## About this repo

Together in the Void is a team project that is still in active
development, so this repository is **not the full source**. It contains
24 files (about 4,900 lines) chosen to show how I structure networking,
ability systems, cutscene/dialogue plumbing, IK and editor tooling. The
character controller, the portal system, level and puzzle scripts,
shaders, UI, scenes, prefabs and all art are not included.

The files keep their original folder layout but will not compile on
their own: they depend on scripts that are not published here and on
third-party packages (Photon Fusion, Final IK, DOTween) that cannot be
redistributed. Code comments are mostly in Thai, my working language;
identifiers and APIs are in English.

## Networking model: Fusion 2 Shared Mode

- **Characters are owner-authoritative.** Each machine has state authority
  over its own character, so movement feels local.
- **The master client owns world objects.** Doors, boulders, wind, traps
  and the lobby are written by the master as `[Networked]` state; other
  machines send RPCs to the master.
- **RPCs are not tick-aligned** (`TickAligned = false`), so they arrive
  immediately instead of waiting for the next tick. The sender always comes
  from `FusionPlayers.SenderOf(Runner, info)`, never from an id the client
  sends.
- **ChangeDetector instead of change callbacks.** Machines follow world
  state with `GetChangeDetector(ChangeDetector.Source.SimulationState)` in
  `Render()`, because `[OnChangedRender]` trails behind by the
  interpolation buffer.
- **The owner reports zones.** "Am I in this zone?" is decided on the
  machine that owns the character and then reported, instead of the master
  guessing from a proxy that lags behind (`LocalZonePresence.cs`).
- **Sessions:** the join code is the Fusion session name and the room
  creator is the master. `FusionConnectionWatcher` handles a partner
  leaving, a lost connection or no internet.

The game was first built on Unity Netcode for GameObjects with Unity
Relay. In October 2026 I moved the whole project to Fusion 2 in six
milestones, testing each one in Multiplayer Play Mode, then removed NGO,
Relay and Vivox.

## What's here

**Networking (`Scripts/Network/`)**
- `FusionSession.cs`: start or join a Shared Mode session by code, load
  scenes, leave, and keep host / slot / solo state.
- `FusionPlayers.cs`: find a player from a collider or `PlayerRef`;
  `SenderOf` for RPCs.
- `FusionPlayerSpawner.cs`, `FusionProxySpawn.cs`: per-scene spawning, and
  snapping proxies to their network state on the first frame.
- `FusionConnectionWatcher.cs`: partner left / connection lost → back to
  the menu with a message.
- `LocalZonePresence.cs`: owner-reported zone counting.
- `CoopHoldVote.cs` + `Scripts/Timeline/CutsceneSkipVote.cs`: "both
  players hold E" base class, decided by the master.

**Abilities (`Scripts/PlayerAbility/`)**
- `AimedAbility_Net.cs`: shared base for aim-and-fire abilities: role and
  ability-manager gates, input, an aim ray through the actual crosshair,
  gamepad aiming where the crosshair snaps to visible targets, and a
  synced aim point for the aim pose on other machines.
- `TimeFreeze/TimeFreezeAbility_Net.cs` + `FreezableNet.cs`: one frozen
  object at a time, decided by the master; objects react through
  `IFreezeListener`.
- `Manager/PlayerAbilityManager.cs`: one `[Networked] int` bitmask;
  `SceneAbilityRule` turns abilities on or off per scene.

**Cutscenes and dialogue**
- `Scripts/Timeline/TimelineController.cs`: Timeline + VideoPlayer
  cutscenes where the video follows `director.time`, so the picture stays
  on the audio clock through frame hitches, with re-sync on drift, fades,
  skip-to-end and chaining.
- `Scripts/Dialogue/DialogueDirector.cs`: the master owns a priority queue
  (Story > Hint > Banter) and starts a sequence on every machine; each
  machine plays the lines with the same timing, measured from the English
  source, so players reading different languages stay in step.

**Animation (Final IK)**
- `Player/Scripts/PlayerFootIK.cs`: drives the Grounder (fade in/out,
  reset after teleports, slower foot tracking on slopes, stable knee
  direction when the leg is nearly straight).
- `Player/Scripts/PlayerAimPose.cs`: *It Takes Two*-style aim pose spread
  over spine, shoulder and neck bones on top of the animation.

**Systems and tools**
- `Scripts/System/ShaderWarmup.cs`: warms shader variants and VFX during
  the loading screen to remove first-use hitches.
- `Editor/SoloPlay/SoloPlayMenu.cs`: one-key solo testing of any scene.
- `Editor/LedgeGrab/LedgeReachScanner.cs`: finds ledges the ledge grab
  makes newly reachable, so designers can block puzzle shortcuts.
- `Editor/Localization/TranslationEditorWindow.cs`: in-editor Thai
  translation editor over a CSV source.

## License

© 2026 Kittichai Panyapa. All rights reserved.
This code is published for portfolio review only. It is not licensed for
reuse, modification or redistribution. Together in the Void and its
assets belong to the development team.
