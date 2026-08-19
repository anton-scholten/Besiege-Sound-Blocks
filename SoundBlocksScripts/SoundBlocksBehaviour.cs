using System;
using System.Collections.Generic;
using Modding;
using Modding.Modules;
using UnityEngine;

// Modding.Serialization is deliberately not imported: it declares its own Vector3,
// which would make every UnityEngine.Vector3 here ambiguous. Its one type used
// here, ResourceReference, is spelled out in full instead.

namespace SoundBlocksMod
{
    /// <summary>
    /// The block's behaviour: builds the mapper controls, loads the chosen clip and
    /// drives the AudioSource during simulation.
    /// </summary>
    public class SoundBlocksBehaviour : BlockModuleBehaviour<SoundBlocks>
    {
        private MKey PlayKey;
        private MSlider VolumeSlider;
        private float vol;
        private MToggle PushToggle;
        private bool PlayingToggleAudio;
        private MToggle Loop;
        private MToggle TimeDependent;
        private MSlider PitchSlider;
        private MToggle DistDependent;
        private MToggle BurnEffect;
        private MMenu SoundFileMenu;
        private MMenu SpecialMenu;

        public AudioSource source_audio;

        private bool hasStarted;
        private int startFrames;
        private float SpeedUp;
        private bool BurningNow;
        private float PitchFactor = 1f;

        private MToggle SpecialToggle;
        private AudioClip SpecialClip1;
        private AudioClip SpecialClip2;
        private AudioClip SpecialClip3;
        private bool SpecialFlag;
        private bool canPlay;

        // One list per block. Static was wrong three times over: MenuHandler
        // appends in every block's SafeAwake and never clears, so the menu grew by
        // a whole clip list per block placed; MMenu keeps the list by reference,
        // so a second block rewrote the first one's menu; and MMenu.Selection
        // indexes unchecked, so a machine saved against a longer list came back
        // playing the wrong sound or throwing out of SimulateUpdateAlways.
        private List<string> SoundNames = new List<string>();
        private List<string> ModeNames = new List<string>();

        // Found here rather than through ModBlockBehaviour.IsBurning, which reads
        // a FireController the handler looks up on the block's *root* only --
        // while BlockPrefabCreator.SetupFire configures the one on the child named
        // "FireController". Where those differ, IsBurning is false all simulation
        // and the burn effect never fires. GetComponentInChildren covers both.
        private FireController fireController;
        private bool searchedForFire;

        private Vector3 prevPos;
        private Vector3 velocityVector;
        private MValue MinPitch;
        private MValue MaxPitch;
        private MSlider MaxDist;

        // The velocity effect is two toggles now. Either one on enables it, and
        // with both on the livelier of the two motions drives the pitch.
        private MToggle Translation;
        private MToggle Rotation;

        // Superseded by Translation/Rotation and never shown, but still
        // registered: 0.1.5 saved its velocity settings under these keys, and an
        // unregistered entry is not deserialised at all, so dropping them would
        // lose the setting rather than carry it over.
        private MToggle VeloDependent;
        private MMenu VeloTraRot;
        private bool migratedVelocity;

        public override void SafeAwake()
        {
            source_audio = GetComponent<AudioSource>();
            if (source_audio == null)
            {
                source_audio = gameObject.AddComponent<AudioSource>();
            }

            PlayKey = AddKey("Play Sound", "Activate", KeyCode.P);
            VolumeSlider = AddSlider("Volume", "Volume", 1f, 0f, 1f);
            PitchSlider = AddSliderUnclamped("Pitch", "Pitch", 1f, -5f, 5f);
            PushToggle = AddToggle("Toggle", "Toggle", true);
            Loop = AddToggle("Loop", "Loop", false);
            TimeDependent = AddToggle("TimeScale", "TimeDependent", true);
            DistDependent = AddToggle("Distance", "DistDependent", true);
            Translation = AddToggle("Translation", "TranslationKey", false);
            Rotation = AddToggle("Rotation", "RotationKey", false);
            BurnEffect = AddToggle("Burn effect", "BurnEffect", true);
            SpecialToggle = AddToggle("Special Mode", "SpecialToggle", false);

            // Registered only so 0.1.5 machines still deserialise; see
            // EnsureVelocityMigrated.
            VeloDependent = AddToggle("Velocity", "VeloDependent", false);
            VeloDependent.DisplayInMapper = false;

            MenuHandler();

            MinPitch = AddValue("Min velocity pitch", "MinPitchKey", 0.5f);
            MaxPitch = AddValue("Max velocity pitch", "MaxPitchKey", 1.5f);
            MaxDist = AddSlider("Max Dist", "MaxDistKey", 250f, 0f, 500f);
            VeloTraRot = AddMenu("VeloTraRotKey", 0, new List<string> { "Translation", "Rotation" }, false);
            VeloTraRot.DisplayInMapper = false;

            DistDependent.Toggled += new ToggleHandler(DistanceDep);
            Translation.Toggled += new ToggleHandler(VelocityDep);
            Rotation.Toggled += new ToggleHandler(VelocityDep);
            SpecialToggle.Toggled += new ToggleHandler(SpecialMode);

            // Not EnsureVelocityMigrated() here: SafeAwake runs before the saved
            // data is deserialised, so it would read defaults, find nothing to
            // carry over and set the guard that stops the real migration ever
            // running. Simulation start and opening the mapper both drive it.
            VelocityDep(false);
        }

        /// <summary>
        /// The mapper's rows, top to bottom. A pair shares a row; a single keeps
        /// one to itself. Sliders, values and menus are left where the mapper put
        /// them.
        /// </summary>
        public List<MapperType[]> LayoutRows()
        {
            List<MapperType[]> rows = new List<MapperType[]>();
            rows.Add(new MapperType[] { PushToggle, Loop });
            rows.Add(new MapperType[] { TimeDependent, DistDependent });
            rows.Add(new MapperType[] { Translation, Rotation });
            rows.Add(new MapperType[] { BurnEffect, SpecialToggle });

            // The sliders are listed one per row purely to fix their order: left
            // alone the mapper interleaves them by widget type, landing the two
            // MValues among the MSliders.
            rows.Add(new MapperType[] { VolumeSlider });
            rows.Add(new MapperType[] { PitchSlider });
            rows.Add(new MapperType[] { MaxDist });
            rows.Add(new MapperType[] { MinPitch });
            rows.Add(new MapperType[] { MaxPitch });
            return rows;
        }

        /// <summary>
        /// Builds the two menus out of what the block's XML declared: every clip in
        /// &lt;Sounds&gt; and every entry in &lt;CustomMode&gt;.
        /// </summary>
        private void MenuHandler()
        {
            // Both lists are [CanBeEmpty], so the loader leaves the array null if
            // the XML declares neither.
            //
            // One entry per declared sound, always, even when the resource does not
            // resolve: a machine stores its choice as an *index*, so dropping an
            // entry would repoint every saved block after it at the wrong clip. The
            // reference's Name is what GetAudioClip wants anyway.
            if (Module.Sounds != null)
            {
                foreach (object sound in Module.Sounds)
                {
                    Modding.Serialization.ResourceReference reference =
                        (Modding.Serialization.ResourceReference)sound;
                    ModAudioClip clip = GetResource(reference) as ModAudioClip;
                    SoundNames.Add(clip != null ? clip.Name : reference.Name);
                }
            }
            SoundFileMenu = AddMenu("SoundMenuKey", 0, SoundNames, false);

            if (Module.Modes != null)
            {
                foreach (Mode mode in Module.Modes)
                {
                    ModeNames.Add(mode.Name);
                }
            }
            SpecialMenu = AddMenu("SpecialMenuKey", 0, ModeNames, false);
        }

        /// <summary>
        /// Turns a menu's saved index into one this block's list can be indexed
        /// by. Returns -1 for an empty menu: "nothing to play".
        ///
        /// Necessary because MMenu neither bounds-checks the index it deserialises
        /// nor the one it indexes with, and 0.1.5 machines *are* out of range --
        /// its static list held one copy of the clip list per block placed, so a
        /// third block's selection was an index into the third copy.
        ///
        /// A modulo, not a clamp: the old list was this list repeated, so index 61
        /// of a 3x28 list is index 5 of a 28 list -- the same sound. Clamping
        /// would silently substitute the last entry in the menu.
        /// </summary>
        private static int MenuIndex(MMenu menu, List<string> items)
        {
            if (items.Count == 0)
            {
                return -1;
            }
            int value = menu.Value;
            if (value < 0)
            {
                return 0;
            }
            return value % items.Count;
        }

        /// <summary>True when either motion source is driving the pitch.</summary>
        private bool VelocityActive
        {
            get { return Translation.IsActive || Rotation.IsActive; }
        }

        // Special Mode replaces the single-clip controls with the mode menu.
        private void SpecialMode(bool toggleState)
        {
            PushToggle.DisplayInMapper = !toggleState;
            Loop.DisplayInMapper = !toggleState;
            SoundFileMenu.DisplayInMapper = !toggleState;
            SpecialMenu.DisplayInMapper = toggleState;
        }

        private void DistanceDep(bool toggleState)
        {
            MaxDist.DisplayInMapper = toggleState;
        }

        /// <summary>
        /// Both velocity toggles land here, so the limits follow "either one is
        /// on" rather than whichever was clicked last -- hence the ignored
        /// argument, which the ToggleHandler signature requires.
        /// </summary>
        private void VelocityDep(bool toggleState)
        {
            bool active = VelocityActive;
            MinPitch.DisplayInMapper = active;
            MaxPitch.DisplayInMapper = active;
        }

        /// <summary>
        /// Carries a 0.1.5 machine onto the two velocity toggles that replaced the
        /// Velocity toggle and its Translation/Rotation menu. Runs once, and only
        /// while the new toggles are untouched, so a re-saved machine is left be.
        /// </summary>
        public void EnsureVelocityMigrated()
        {
            if (migratedVelocity)
            {
                return;
            }
            migratedVelocity = true;

            if (!VeloDependent.IsActive)
            {
                return;             // velocity was off, nothing to carry over
            }
            if (!Translation.isDefaultValue || !Rotation.isDefaultValue)
            {
                return;             // already set on this machine; leave it alone
            }

            // The old menu was exclusive: entry 0 Translation, entry 1 Rotation.
            if (VeloTraRot.Value == 0)
            {
                Translation.IsActive = true;
            }
            else
            {
                Rotation.IsActive = true;
            }
            // Setting IsActive directly does not raise Toggled, so the pitch
            // limits have to be shown by hand.
            VelocityDep(true);
        }

        /// <summary>
        /// Besiege keeps the machine, and these behaviours, alive between runs and
        /// none of this state was ever put back. The burn effect was the visible
        /// case: once SpeedUp passed 1000 a block that had burned once stayed
        /// silent for every later run, until the machine was reloaded.
        /// </summary>
        public override void OnSimulateStart()
        {
            hasStarted = false;
            startFrames = 0;
            canPlay = false;
            vol = 0f;
            SpeedUp = 0f;
            PitchFactor = 1f;
            BurningNow = false;
            PlayingToggleAudio = false;
            SpecialFlag = false;
            prevPos = gameObject.transform.position;
            // Look the FireController up again: the block is re-prefabbed between
            // runs, so a reference cached in an earlier one can be stale.
            searchedForFire = false;
            fireController = null;
            NormaliseMenus();
            EnsureVelocityMigrated();
        }

        /// <summary>
        /// Writes a repaired index back into the menus, so a 0.1.5 machine stops
        /// carrying an out-of-range one around: MenuIndex copes at every read, but
        /// only this makes the mapper show the right entry and makes re-saving
        /// write an index the next load need not repair.
        ///
        /// At simulation start, not in SafeAwake: the saved data is deserialised
        /// after SafeAwake, so there would be nothing to repair yet.
        /// </summary>
        private void NormaliseMenus()
        {
            int sound = MenuIndex(SoundFileMenu, SoundNames);
            if (sound >= 0 && SoundFileMenu.Value != sound)
            {
                SoundFileMenu.SetValue(sound);
            }
            int mode = MenuIndex(SpecialMenu, ModeNames);
            if (mode >= 0 && SpecialMenu.Value != mode)
            {
                SpecialMenu.SetValue(mode);
            }
        }

        public override void SimulateUpdateAlways()
        {
            // One-time setup, deferred to the second simulated frame: the mapper's
            // values are not settled in the first one.
            if (!hasStarted)
            {
                if (startFrames == 1)
                {
                    hasStarted = true;

                    // Who is allowed to play this block's sound. Singleplayer plays
                    // unconditionally; in multiplayer the host and clients each only
                    // play once their own play mode has started.
                    if (!StatMaster.isClient && !StatMaster.isHosting)
                    {
                        canPlay = true;
                        SpecialSwitcher();
                    }
                    else if (StatMaster.isHosting && StatMaster.InGlobalPlayMode)
                    {
                        canPlay = true;
                        SpecialSwitcher();
                    }
                    else if (StatMaster.isClient)
                    {
                        if (StatMaster.InGlobalPlayMode || StatMaster.InLocalPlayMode)
                        {
                            canPlay = true;
                            SpecialSwitcher();
                        }
                    }
                    else
                    {
                        canPlay = false;
                        return;
                    }

                    vol = VolumeSlider.Value;
                    if (vol <= 0f)
                    {
                        vol = 0f;
                        return;
                    }

                    source_audio.volume = vol;
                    source_audio.loop = Loop.IsActive;
                    source_audio.minDistance = 0f;
                    source_audio.maxDistance = MaxDist.Value;
                    source_audio.rolloffMode = AudioRolloffMode.Linear;
                    source_audio.dopplerLevel = 0.01f;
                    // 0 = 2D (heard everywhere), 1 = 3D (falls off with distance).
                    source_audio.spatialBlend = Convert.ToSingle(DistDependent.IsActive);

                    if (SpecialToggle.IsActive)
                    {
                        source_audio.loop = false;
                    }
                }
                else
                {
                    startFrames++;
                }
            }

            if (!canPlay)
            {
                hasStarted = false;
                return;
            }
            if (vol == 0f)
            {
                return;
            }

            source_audio.pitch = PitchSlider.Value * PitchFactor;
            source_audio.pitch = source_audio.pitch * (TimeDependent.IsActive ? Time.timeScale : 1f);

            // Differentiated every frame whether or not Translation is on, so
            // switching it on mid-run does not read a stale prevPos as one enormous
            // jump. deltaTime is zero at timeScale 0, which would divide by zero.
            if (Time.deltaTime > 0f)
            {
                velocityVector = (gameObject.transform.position - prevPos) / Time.deltaTime;
                prevPos = gameObject.transform.position;
            }

            if (VelocityActive)
            {
                float velocityPitch = 0f;
                if (Translation.IsActive)
                {
                    velocityPitch = velocityVector.magnitude / 100f;
                }
                if (Rotation.IsActive)
                {
                    Rigidbody body = gameObject.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        // With both on the livelier motion wins; with one on this
                        // is what the old Translation/Rotation menu did.
                        float spin = body.angularVelocity.magnitude / 50f;
                        if (spin > velocityPitch)
                        {
                            velocityPitch = spin;
                        }
                    }
                }

                if (velocityPitch < MinPitch.Value)
                {
                    velocityPitch = MinPitch.Value;
                }
                else if (velocityPitch > MaxPitch.Value)
                {
                    velocityPitch = MaxPitch.Value;
                }
                source_audio.pitch = source_audio.pitch * velocityPitch;
            }

            if (SpecialToggle.IsActive)
            {
                SpecialModePlayer();
                return;
            }

            float input = PlayKey.Value;
            if (PlayKey.IsPressed)
            {
                if (PlayingToggleAudio)
                {
                    source_audio.Stop();
                    PlayingToggleAudio = false;
                }
                else
                {
                    source_audio.Play();
                    if (PushToggle.IsActive)
                    {
                        PlayingToggleAudio = true;
                    }
                }
            }

            if (input == 0f)
            {
                if (source_audio.isPlaying)
                {
                    // Hold-to-play: releasing the key cuts the clip short.
                    if (!PushToggle.IsActive)
                    {
                        source_audio.Stop();
                    }
                }
                else if (!Loop.IsActive && PushToggle.IsActive && PlayingToggleAudio)
                {
                    // A one-shot in toggle mode ended on its own; re-arm the toggle.
                    PlayingToggleAudio = false;
                }
            }
        }

        /// <summary>
        /// Picks which clip (or clip set) this block will play, from the mapper.
        /// </summary>
        private void SpecialSwitcher()
        {
            if (SpecialToggle.IsActive)
            {
                int index = MenuIndex(SpecialMenu, ModeNames);
                if (index < 0)
                {
                    ModConsole.Log("Special Mode is on but this block declares no modes; nothing to play.");
                    return;
                }
                Mode mode = Module.Modes[index];
                string clip1 = mode.Sound_1;
                string clip2 = mode.Sound_2;
                string clip3 = mode.Sound_3;
                SpecialModeLoader(clip1, clip2, clip3);
            }
            else
            {
                int index = MenuIndex(SoundFileMenu, SoundNames);
                if (index < 0)
                {
                    ModConsole.Log("This block declares no sounds; nothing to play.");
                    return;
                }
                SoundLoader(SoundNames[index]);
            }
        }

        private void SoundLoader(string soundName)
        {
            try
            {
                source_audio.clip = ModResource.GetAudioClip(soundName);
            }
            catch (Exception ex)
            {
                ModConsole.Log("Failed to load sound file!");
                ModConsole.Log(ex.ToString());
            }
        }

        private void SpecialModeLoader(string clip1, string clip2, string clip3)
        {
            try
            {
                SpecialClip1 = ModResource.GetAudioClip(clip1);
                SpecialClip2 = ModResource.GetAudioClip(clip2);
                SpecialClip3 = ModResource.GetAudioClip(clip3);
                source_audio.clip = SpecialClip1;
            }
            catch (Exception ex)
            {
                ModConsole.Log("Failed to load Special Mode sound files! Block will be destroyed to reduce lag. ");
                ModConsole.Log(ex.ToString());
                UnityEngine.Object.Destroy(this);
            }
        }

        /// <summary>
        /// Special Mode: clip 1 on press, clip 2 looping once clip 1 has finished,
        /// clip 3 on release. An engine start / idle / shutdown, in other words.
        /// </summary>
        private void SpecialModePlayer()
        {
            if (PlayKey.IsPressed)
            {
                source_audio.Stop();
                source_audio.clip = SpecialClip1;
                source_audio.loop = false;
                source_audio.Play();
                SpecialFlag = true;
            }

            if (PlayKey.IsDown)
            {
                if (SpecialFlag && !source_audio.isPlaying)
                {
                    source_audio.clip = SpecialClip2;
                    source_audio.loop = true;
                    source_audio.Play();
                    SpecialFlag = false;
                }
            }

            if (PlayKey.IsReleased)
            {
                source_audio.clip = SpecialClip3;
                source_audio.loop = false;
                source_audio.Play();
            }
        }

        /// <summary>
        /// True while the block is alight. Prefers the block's own FireController,
        /// wherever on the block it sits, and falls back to the base property.
        /// </summary>
        private bool BlockIsOnFire()
        {
            if (!searchedForFire)
            {
                searchedForFire = true;
                fireController = GetComponentInChildren<FireController>();
            }
            if (fireController != null && fireController.onFire)
            {
                return true;
            }
            return IsBurning;
        }

        /// <summary>
        /// Burn effect: on fire, the pitch climbs and the volume falls until the
        /// block gives out.
        /// </summary>
        public override void SimulateFixedUpdateAlways()
        {
            if (BurnEffect.IsActive)
            {
                if (BlockIsOnFire() || BurningNow)
                {
                    if (SpeedUp >= 1000f)
                    {
                        source_audio.loop = false;
                        source_audio.Stop();
                        vol = 0f;
                        BurningNow = false;
                    }
                    else if (SpeedUp >= 400f)
                    {
                        PitchFactor += 0.002f;
                        source_audio.volume = source_audio.volume - 0.004f;
                        SpeedUp += 1f;
                    }
                    else
                    {
                        PitchFactor += 0.001f;
                        SpeedUp += 1f;
                        BurningNow = true;
                    }
                }
            }
        }
    }
}
