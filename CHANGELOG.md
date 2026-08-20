# Changelog

## Unreleased

**Changed**

- The mod now lives in a `SoundBlocks/` subfolder rather than being the whole
  repository, matching the sibling mods. `.git` was inside the folder Besiege
  copies when publishing, and its read-only objects then jammed the Workshop
  uploader for every mod until the staging folder was deleted by hand. See
  [AGENTS.md](AGENTS.md#layout).

## 0.3.1

Everything below is on top of 0.1.5, the last released version. Machines built
with that version load and play as they did.

**Fixed**

- Placing more than one sound block corrupted the sound menu: the clip list was
  shared between blocks, so the menu grew by a whole copy per block placed and
  blocks played the wrong sound — or threw, and stopped working for the rest of
  the run. Saved machines with the old, overlong index are repaired on load.
- The burn effect never triggered on blocks whose FireController sits on a child
  object, which is most of them.
- A block that had burned once stayed silent for every later simulation run
  until the machine was reloaded. Per-run state is now reset at simulate.
- **TimeScale** off now really leaves the pitch alone.
- A negative **Pitch** only played the clip backwards with **Loop** on. Playback
  starts at the end of the clip now, so reverse works as a one-shot too.
- Special Mode on a block whose XML declares no modes no longer divides by zero.

**Changed**

- The options pane is laid out two toggles to a row, and sizes itself to what is
  showing: turning on Distance or a velocity mode adds its slider and the window
  grows, and it shrinks again when they go. Nothing scrolls.
- The Velocity toggle and its Translation/Rotation pop-up are now two toggles,
  **Translation** and **Rotation**. Either one shows the min and max velocity
  pitch values; with both on, the livelier motion drives the sound. Existing
  machines carry their old setting over.
- Sliders read in order: volume, pitch, max distance, min and max velocity pitch.

The source was recovered from the shipped assembly — the original was lost. See
`docs/RECOVERY.md`.
