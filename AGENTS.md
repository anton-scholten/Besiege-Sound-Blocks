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

**`VeloDependent` and `VeloTraRotKey` are dead controls that must stay
registered.** The Velocity toggle and its Translation/Rotation menu became two
toggles, `TranslationKey` and `RotationKey`. The old pair is still declared and
immediately hidden, purely so a machine saved by the released **0.1.5** still
deserialises them — an unregistered mapper entry is not read back at all, so
deleting them would drop the setting rather than carry it over.
`EnsureVelocityMigrated` reads them once, and only while both new toggles are at
their defaults, so a re-saved machine is never second-guessed. It cannot run in
`SafeAwake`, which is before the block's data is deserialised.

Only 0.1.5 needs this. The 0.1.6 and 0.1.7 builds were never released, so no
machine exists that was saved by them.

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

**Keep one menu entry per declared sound, even one that fails to resolve.**
`MenuHandler` falls back to the `ResourceReference`'s own name rather than
skipping the entry, for the same reason: dropping one shifts every index after
it.

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

The lists are per-instance now, and `MenuIndex` repairs the index before any
read. Both halves are needed: per-instance lists fix new machines, the repair is
what keeps machines saved by the released 0.1.5 working.

**That repair is a modulo, not a clamp**, and the difference matters. The old
list was this exact list repeated once per sound block, so the third block's
choice of sound 5 was stored as 2×28 + 5 = 61. `61 % 28` is 5 — the clip the
machine was actually saved with. Clamping to `Count - 1` would have quietly
substituted the last sound in the menu instead. `NormaliseMenus` then writes the
repaired index back at simulation start, so the mapper shows the right entry and
re-saving the machine stores an index that needs no repair next time.

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

**The burn effect could never see the fire.** It gated on
`ModBlockBehaviour.IsBurning`, which is `handler.fireController.onFire` — and the
handler fills `fireController` in with a `GetComponent<FireController>()` on the
block's **root**, while `BlockPrefabCreator.SetupFire` configures the controller
it finds on the child transform named `"FireController"`
(`SerializationHelpers.SetOnObject` also refuses to wire fire up at all when that
child is missing, warning *"Cannot add FireInteraction during a reload, restart
the game!"* — so a mod hot-reload leaves it unwired until a full restart). When
the root has no `FireController`, `IsBurning` is false for the whole simulation.
`BlockIsOnFire()` now reads `FireController.onFire` — a public field on a type
the blacklist does not cover — via `GetComponentInChildren`, and keeps
`IsBurning` as a fallback.

## The compact mapper layout

`MapperLayout` packs the toggles two to a row. **Besiege has no supported way to
do this**, so read this before touching it.

`MapperType` carries no width or column; the mapper stacks one widget per row
through `GenericController<T>` fields that are private on `BlockMapper`, and
reflection is blacklisted. The sanctioned custom-widget route
(`CustomMapperTypes.AddMapperType`) is closed too — a selector is instantiated
from `WidgetController.prefabPath`, a Besiege `Resources` path a mod cannot add
to. So this restacks the rows the mapper has already built, reaching them through
public members only: `BlockMapper.CurrentInstance`/`IsOpen`/`IsBlock`/`Block`,
and `ContainerDetails`' `Top`/`Bottom` setters and `selector`/`Background`
fields.

### Four things that are not optional

**Restore on close.** The mapper pools its rows, so a row left half-width comes
back half-width in the next block's mapper, base-game blocks included.

**Re-apply every frame, and rebuild first.** `BlockMapper.LateUpdate` calls
`Rebuild()` whenever the mapper is dirty — which showing or hiding a slider makes
it — putting every row back at stock *after* `Tick` has run, so that frame draws
the one-column layout. `Tick` therefore calls `Rebuild()` itself when `IsDirty`;
it ends by clearing the flag, so `LateUpdate` finds nothing to do and the
re-layout lands before anything is drawn. `Apply` must also stay **idempotent**:
it restacks from measured values and takes the column width from the widest
*unpaired* row, never by halving whatever it finds — halving relatively would
halve again every frame until the buttons vanished.

**`ColumnGap` is 0.** What gets halved is the row's backing plate, not the
button, so any gap shows as a lighter seam of bare panel down the middle of the
toggle block. The buttons have their own inset, so butted plates still look
separated.

**`LayoutRows` lists the sliders too**, one per row. That is only to fix their
order: left alone the mapper interleaves `MValue`s among the `MSlider`s.

**Drive it from `MapperLayoutHost`, not from the block.** Unity stops a
MonoBehaviour's coroutines when it is disabled, which is what happens to
build-area blocks during simulation — a loop hosted on the block died on the
first simulate and never came back. The host is a `DontDestroyOnLoad` object
created in `Mod.OnLoad`.

**Work in world units.** `Top`/`Bottom` are world-space edges and rows are
contiguous: one row's `Bottom` is the next one's `Top`, `position.y` is the
midpoint, `Height` the difference. But `localPosition` is in a *different* scale
— the rows' parent is scaled 0.9, so a local pitch of 0.6 is a world pitch of
0.54. Mixing them is wrong by 10% per row and compounds down the stack. `Apply`
measures the mapping from two real rows and converts back only at the end; widths
alone stay local.

### The panel sizes itself to its content; nothing scrolls

`UpdateBackground` sizes the panel from its `WidgetController`'s `EndPosition`,
which always describes the *uncompacted* layout. `set_EndPosition` is private and
the controller is a private field, so `FitPanel` resizes the panel art itself —
`Background`, found by name from a dump of the hierarchy. `lossyScale` carries the
parent chain, so no scale factor is hardcoded.

**Resize `Background` and nothing else.** `Container/Mask` is the scrollbar's
`contentMask` — a clipping region, which `SetScrollHeight` gives roughly a
screenful of height and then leaves alone. Shrinking it to the sound block's
content clipped every *other* block's rows away too, because one mapper instance
serves them all: the symptom was no options pane at all, on any block.

**Hide the scrollbar with the game's own `DisableScrollbar`.** `UIScrollbar` is
public, and so are `active`, `DisableScrollbar`, `UpdateBounds` and
`contentParent`. `DisableScrollbar` clears `active`, hides the scroller,
everything in `objectsToToggle` and the collider; `Restore` hands control back
with `UpdateBounds`. Reaching past it to disable renderers under the scrollbar is
what blanked the mapper — the pooled rows hang under the same object.

**Besiege will not move the title-bar buttons for us, so `StockScrollbar` does.**
`UpdateBackground` places them as `closeStartPos + right * 0.2 * localScale.x *
0.75` when `scrollbar.active`, and only writes them when `active` *changes*. It
re-measures `active` inside every rebuild, from the stock row layout, before any
of this has compacted anything — so it always concludes the sound block scrolls,
and clearing `active` afterwards is never seen as a change. Hence the offset is
undone by hand, from the same formula, against positions captured once.

**The target is the content's own bottom edge, and the fit grows as well as
shrinks.** Both halves were learned the hard way:

- Every delta-based attempt failed. Shrinking by how far a pass moved things
  reads zero once the rows are already compacted — even though the panel may
  have been re-expanded since, which is exactly what showing or hiding a slider
  does. Shrinking by the summed height difference is right only until it is
  applied twice. A target taken from where the content actually ends can neither
  go stale nor compound.
- Clamping the fit to the game's own panel height meant a newly shown slider
  that pushed the content past it was ignored entirely, which is what left
  Distance, Translation and Rotation looking broken. Without the clamp the window
  simply extends downwards instead.

Each piece is still driven from its remembered *full* size, re-read whenever the
game has written to it since the last pass, so running every frame is stable.

### Never restore a row's position

The mapper **pools** its rows. By the time `Restore` runs, the containers it
recorded may already have been rebuilt into another block's mapper — writing
remembered `Top`/`Bottom` onto those is what once left the Cannon's rows strewn
down its panel. Positions need no undo: the mapper lays every row out itself on
the next `Rebuild`, and `Apply` is idempotent.

A halved *width* does need undoing, because nothing else sets it back, and it is
reverted only where the plate still carries the halved value.

**Every write in `Apply` must be absolute.** It runs each frame with no restore
before it, so anything computed from a value it previously wrote compounds. This
has bitten twice: reading the moved row's own `x` as the column centre marched
the columns a quarter-width left per frame until they left the panel entirely,
and halving whatever width was found would have halved it again every frame. The
centre and the width both come from a row that is never moved; positions come
from `Top`/`Bottom` recomputed from the stack each pass; and the panel shrink is
driven by a saved height that measures as zero once the stack is already
compacted.

### When it goes wrong, measure

The model above was established by logging every row's identity, position,
`Top`/`Bottom`/`Height` and background scale from a running mapper, after two
attempts at guessing the geometry both produced overlapping rows. Do that again
before adjusting anything by eye — a `Debug.Log` loop over
`mapper.GetComponentsInChildren<ContainerDetails>(true)` is the whole of it — so
the arithmetic can be checked on paper against real numbers.

## Known, and not this mod's to fix

`Tried to setup button for nonexistent tooltip 1000` in `Player.log` on startup
is the base game. `Besiege.Tooltips.BlockTooltipController.RebuildTooltips`
builds its table by iterating `Enum.GetValues(typeof(BlockType))`, which is the
base game's own enum — a modded block can never be in it, so
`SetupTooltipButton` logs and returns. Every modded block does this. The cost is
one log line and no hover tooltip on the block's toolbar button.
