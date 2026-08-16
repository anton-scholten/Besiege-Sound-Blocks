# Working on this repository

Notes for anyone — human or AI — changing this mod. The [README](README.md) is
for people who just want to use it; nothing here needs repeating there.

How the C# was recovered from the shipped assembly, and how faithful the result
is, are in [docs/RECOVERY.md](docs/RECOVERY.md).

## Layout

The folder Besiege loads is the repository root, because that is what gets
uploaded to the Workshop. Everything else is beside it and is not part of the
mod.

```
Mod.xml                     manifest: assembly, resources, block list
SoundBlock.xml              the block: mesh, colliders, and the module's data
SoundBlocks.dll             built by tools/build.sh (checked in, the game loads it)
Resources/                  the mesh, the textures and every .ogg the block offers
SoundBlocksScripts/*.cs     mod source
tools/build.sh              compiles with Besiege's own compiler
tools/verify-build.sh       the check to run after editing any .cs
tools/install.sh            builds and installs into the game
sounds/, Previous_promo/    working files; not loaded by anything
```

`SoundBlocks.dll` is committed on purpose. `Mod.xml` names it as an `<Assembly>`,
so a checkout has to carry a built one or the mod does not load.

## Hard rules

**Never change `<ID>` in `Mod.xml`.** The game generates it on first load, and
changing it breaks every saved machine that references the mod. The same goes
for `<ID>1</ID>` in `SoundBlock.xml` and for the module name `SoundBlocksMod`,
which is spelled in three places that must agree: the `[XmlRoot]` on
`SoundBlocks`, the `AddBlockModule` call in `Mod.OnLoad`, and the element name in
`SoundBlock.xml`.

**Do not rename a mapper key.** The second argument to `AddKey`/`AddSlider`/
`AddToggle`/`AddValue` and the first to `AddMenu` (`"Volume"`, `"SoundMenuKey"`,
`"MinPitchKey"`, …) is the key a saved machine stores its setting under. Renaming
one silently resets that setting on every existing machine. The *first* argument
is only the label in the mapper and is free to change.

**Run `./tools/verify-build.sh` after editing any `.cs`.** Besiege's compiler is
ancient — write C# 4: no interpolated strings, no `?.`, no `nameof`, no
expression-bodied members, and no `enum` declarations (they segfault it).

**Do not reorder or remove entries in `<Sounds>` in `SoundBlock.xml`.** A machine
saves its choice as an *index* into that list, so inserting anything but at the
end repoints every saved block at a different sound. Appending is safe.

## Why it is built the way it is

**`System.Xml` is on the mod loader's blacklist and this mod references it
anyway.** That is not an oversight. `InternalModding.Assemblies.AssemblyScanner`
walks field types, method locals and IL operands; it never enumerates custom
attributes. The `[Xml*]` markers on `SoundBlocks` are metadata, so they pass, and
they are the only way to name the elements a block module deserialises.
`tools/build.sh` runs a blacklist check over every build rather than trusting
that reasoning.

**`Modding.Serialization` is deliberately not imported** in
`SoundBlocksBehaviour.cs`. It declares its own `Vector3`, which makes every
`UnityEngine.Vector3` in the file ambiguous. Only `ResourceReference` is needed
from it and it is spelled out at the one place it is used.

**The block is a `BlockModule` plus a `BlockModuleBehaviour<T>`, not a plain
`ModBlockBehaviour`.** That is what lets the clip list live in `SoundBlock.xml`
rather than in code: the loader deserialises `<Sounds>` and `<CustomMode>` into
`SoundBlocks`, and the behaviour reads them back through `Module` to build its
menus. Adding a sound is an XML edit and a file, with no rebuild.

**Setup is deferred to the second simulated frame.** `SafeAwake` builds the
mapper controls, but the *values* in them are not settled until the machine has
been simulating for a frame — hence the `hasStarted` / `startFrames` dance at the
top of `SimulateUpdateAlways` rather than doing the work in `OnSimulateStart`.

**`canPlay` is decided once, from `StatMaster`.** Singleplayer plays
unconditionally; a host plays once it is in global play mode; a client plays in
either global or local. When none of those hold the block sets `canPlay = false`
and clears `hasStarted`, which makes the next frame retry the whole decision —
that is the retry loop, not a bug.

## What was wrong with it

The 2018 assembly was recovered exactly, and then four real defects in it were
fixed. Read this before "simplifying" any of it back.

**The menu lists were `static`.** `SoundNames` and `ModeNames` were static, and
`MenuHandler` — which runs in *every block's* `SafeAwake` — appended the whole
clip list to them and never cleared. Three separate failures came out of that,
and all three get worse the more sound blocks a machine has:

- the menu grew by the full clip list per block placed, so a machine with five
  sound blocks showed each sound five times;
- `MMenu`'s constructor stores the `List<string>` it is handed **by reference**
  and does not copy it, so placing a second block rewrote the first one's menu
  underneath it;
- `MMenu.DeSerialize` writes the saved index with no bounds check and
  `MMenu.Selection` is an unchecked `_items[Value]`, so a machine saved against a
  longer list came back either playing the wrong sound or throwing
  `ArgumentOutOfRangeException` out of `SimulateUpdateAlways`.

The lists are per-instance now, and `MenuIndex` clamps before any read. Both
halves are needed: per-instance lists fix new machines, clamping is what stops
machines saved by the old version from throwing.

**`SpecialSwitcher` divided by zero.** It indexed
`Modes[SpecialMenu.Value % Modes.Length]` on a field marked `[CanBeEmpty]`, so a
block XML declaring no `<CustomMode>` crashed the moment Special Mode was on.

**Nothing was reset between simulation runs.** Besiege keeps the machine, and so
these behaviours, alive when you stop simulating. `SpeedUp`, `PitchFactor`,
`BurningNow`, `PlayingToggleAudio` and `SpecialFlag` were never put back, so a
block that had burned once stayed silent with its pitch wound up for every later
run until the machine was reloaded. `OnSimulateStart` now clears them.

**`MenuHandler` assumed both arrays were non-null and every resource resolved.**
Both are `[CanBeEmpty]`, and the loader leaves the array null when the element is
absent.

## Known, and not this mod's to fix

`Tried to setup button for nonexistent tooltip 1000` in `Player.log` on startup
is the base game. `Besiege.Tooltips.BlockTooltipController.RebuildTooltips`
builds its table by iterating `Enum.GetValues(typeof(BlockType))`, which is the
base game's own enum — a modded block can never be in it, so
`SetupTooltipButton` logs and returns. Every modded block does this. The cost is
one log line and no hover tooltip on the block's toolbar button.
