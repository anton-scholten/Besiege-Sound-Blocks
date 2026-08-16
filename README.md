# Sound Blocks

A Besiege block that plays a sound when you press a key.

Pick a clip from the block's menu, map a key, and it plays. Beyond that the block
is mostly about *how* it plays: hold-to-play or press-to-toggle, looping or
one-shot, pitched by the game's time scale, pitched by how fast the block is
moving or spinning, audible across the map or falling off with distance. Set it
on fire and it winds up and dies like a real speaker.

Multiplayer works — everyone hears everyone's blocks.

## Using it

Place the block, open the block mapper, and set:

| Control | What it does |
| --- | --- |
| **Play Sound** | the key that plays the clip |
| **Sound menu** | which clip |
| **Volume**, **Pitch** | the obvious; pitch is unclamped, so it can run backwards |
| **Toggle** | on, the key starts and stops playback; off, it plays only while held |
| **Loop** | repeat until stopped |
| **TimeScale** | pitch follows the game's slow-motion |
| **Distance** | falls off with distance instead of being heard everywhere; **Max Dist** sets how far |
| **Velocity** | pitch rises with speed, between **Min** and **Max velocity pitch**, from either translation or rotation |
| **Burn effect** | the block winds up and gives out while on fire |
| **Special Mode** | a three-clip engine: start on press, a loop while held, and a shutdown on release |

## Adding your own sounds

No rebuild needed — the clip list lives in the block's XML.

1. Drop an `.ogg` in `Resources/Sounds/`.
2. Declare it in `Mod.xml` as an `<AudioClip name="..." path="Sounds\yours.ogg" />`.
3. **Append** it to `<Sounds>` in `SoundBlock.xml`.

Append, do not insert. A machine saves its choice as an index into that list, so
inserting anywhere but the end repoints every already-saved block at a different
sound.

A Special Mode entry is the same idea with three clips:

```xml
<Mode name="Car" Sound_1="Car1ClipA" Sound_2="Car1ClipB" Sound_3="Car1ClipC" />
```

`Sound_1` plays on press, `Sound_2` loops once the first finishes, `Sound_3`
plays on release.

## Building

The mod ships with `SoundBlocks.dll` already built, so you only need this to
change the code. There is no .NET toolchain to install — the build uses
Besiege's own compiler:

```sh
./tools/build.sh          # compile SoundBlocks.dll
./tools/verify-build.sh   # compile without overwriting it
./tools/install.sh        # build, then copy into your Besiege install
```

Set `BESIEGE_DIR` if your install is somewhere the scripts do not look. Besiege
reads mods once at startup, so restart the game to pick up a new build.

The source in `SoundBlocksScripts/` was recovered from the shipped assembly after
the original was lost; [docs/RECOVERY.md](docs/RECOVERY.md) covers how, and
[AGENTS.md](AGENTS.md) is the working notes for changing it.

## Credits

Mod by wizz6rd. The bundled sound effects come from various places around the
internet and are included for use in-game.
