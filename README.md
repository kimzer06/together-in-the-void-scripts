# Together in the Void — Code Sample

3D co-op asymmetric platformer for 2 players, built in Unity3D with a
server-authoritative multiplayer architecture. Senior thesis project —
role: **Lead Gameplay & Network Programmer** (sole programmer on a
6-person team: 1 game designer, 2 2D artists, 1 3D artist, 1
animator/rigger).

▶ Playable build: `[[ add your itch.io link here ]]`
▶ Gameplay video: `[[ add a short YouTube/Drive demo link here ]]`

## About this repo

This repository contains **only the original C# scripts I wrote** —
`Assets/Together In the void/Scripts/`. It intentionally excludes
Unity's generated folders (`Library/`, `Temp/`, `obj/`) and every
third-party Asset Store package used by the project (DOTween, FishNet,
etc.), per their license terms, which do not permit redistributing the
asset files themselves.

That means **this repo will not compile or open as a runnable Unity
project as-is** — it exists as a code sample for review, not a clone
target. To see the game running, use the playable build linked above.

## Tech stack

Unity3D · C# · Unity Netcode for GameObjects (NGO) · Unity Relay / UTP
· DOTween · Unity Localization

## Highlights

- **Server-authoritative multiplayer** — `NetworkVariable`/`NetworkList`,
  `ServerRpc`/`ClientRpc`, all state changes validated on the host.
- **Relay & matchmaking** — join-code based host/client connection via
  Unity Relay (`Scripts/QuickTestRelayLauncher.cs`,
  `Scripts/Character Select/RelayMenuSingleScene.cs`).
- **Interface-driven gameplay systems** — e.g. `ISwitchableWindManager`,
  `IActivatable`, `IFreezeListener`, `ITrapCycle`, `ISlideZone` — used to
  decouple puzzle mechanics from the objects that trigger them.
- **Performance** — `MaterialPropertyBlock` used instead of per-object
  material instances to cut draw calls (`Scripts/PlatformsController/DisappearPlatform.cs`).
- **In-editor developer console** (`~` key) for fast host/join testing
  during multiplayer debugging.

## Folder guide

```
Scripts/
  Character Select/   room join, character select, scene load sync
  PlayerAbility/       Blink, TimeFreeze, Portal — all *_Net.cs are networked
  PlatformsMove/Fan/   wind zone system (push/flyup/heat-death zones)
  Switch/              interactive switches & wind puzzle controllers
  Boulder/             rolling boulder trap & puzzle pillar system
  SlideZone/            spline-based slide sections
```

---
*Third-party assets used in the full project (not included here):
DOTween, FishNet, TextMesh Pro, and others — each remains the property
of its respective publisher and is licensed for use within the
compiled game only.*
