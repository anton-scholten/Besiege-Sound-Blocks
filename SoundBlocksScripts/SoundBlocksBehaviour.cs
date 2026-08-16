using System;
using System.Collections.Generic;
using Modding;
using Modding.Modules;
using UnityEngine;

// Modding.Serialization is deliberately not imported: it declares its own Vector3,
// which would make every UnityEngine.Vector3 here ambiguous. Only ResourceReference
// is needed from it, and it is spelled out at the one place it is used.

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
        private AudioClip audioClip;

        private bool hasStarted;
        private int startFrames;
        private float input;
        private float SpeedUp;
        private bool BurningNow;
        private float PitchFactor = 1f;
        public static bool localPitch;

        private MToggle SpecialToggle;
        private AudioClip SpecialClip1;
        private AudioClip SpecialClip2;
        private AudioClip SpecialClip3;
        private bool SpecialFlag;
        private bool canPlay;

        // One list per block, not one shared by all of them. These used to be
        // static, which was wrong three times over: MenuHandler appends to them in
        // every block's SafeAwake and never clears, so the menu grew by the whole
        // clip list per block placed; MMenu stores the list it is handed by
        // reference rather than copying it, so placing a second sound block
        // silently rewrote the first one's menu; and MMenu.Selection indexes
        // _items unchecked, so a machine saved against a longer list came back
        // either playing the wrong sound or throwing out of SimulateUpdateAlways.
        private List<string> SoundNames = new List<string>();
        private List<string> ModeNames = new List<string>();

        // Read directly rather than through ModBlockBehaviour.IsBurning. That
        // property is handler.fireController.onFire, and the handler fills
        // fireController in with a GetComponent on the block's *root* only --
        // whereas BlockPrefabCreator.SetupFire configures the one it finds on the
        // child transform named "FireController". When those are not the same
        // object the property is false for the whole simulation and the burn
        // effect never fires. GetComponentInChildren covers both placements.
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

        // Superseded by Translation/Rotation and never shown. They stay
        // registered because the released 0.1.5 stored its velocity settings under
        // these keys, and an unregistered mapper entry is not deserialised at all
        // -- so dropping them would lose the setting rather than carry it over.
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

            // Registered only so 0.1.7-and-earlier machines still deserialise;
            // MigrateVelocity reads them and they are never shown.
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

            // Not MigrateVelocity() here: SafeAwake runs before the machine's saved
            // data is deserialised into the mapper, so it would read defaults, find
            // nothing to carry over, and set the guard that stops the real
            // migration from ever running. It is driven from OnSimulateStart and
            // from opening the mapper, both of which are after the load.
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

            // The sliders are listed too, one per row, purely to fix their order.
            // Left alone the mapper interleaves them by widget type -- the two
            // MValues land among the MSliders, giving min pitch, volume, max pitch,
            // pitch, max dist. Naming them here puts them in reading order.
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
        public void MenuHandler()
        {
            // Both lists are marked [CanBeEmpty], so a block XML is allowed to
            // declare neither, and the loader leaves the array null in that case.
            //
            // Exactly one entry per declared sound, always, even when the resource
            // does not resolve. A machine stores its choice as an *index* into this
            // list, so silently dropping an entry would shift every sound after it
            // and repoint saved blocks at the wrong clip. The reference's own Name
            // is the same string ModResource.GetAudioClip wants, so the fallback
            // still plays if the resource turns up later.
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
        /// Turns a menu's saved index into one this block's list can be indexed by.
        /// Returns -1 for an empty menu, which every caller treats as "nothing to
        /// play".
        ///
        /// Two things make this necessary. MMenu.DeSerialize writes the saved index
        /// with no bounds check and MMenu.Selection is an unchecked _items[Value],
        /// so an out-of-range index throws out of SimulateUpdateAlways where
        /// nothing can report it. And machines saved by 0.1.5 or earlier *are*
        /// out of range: the menu list was static and every block's SafeAwake
        /// appended the whole clip list to it again, so a machine with three sound
        /// blocks built a list of three identical copies and the third block's
        /// selection was stored as an index into the third copy.
        ///
        /// The repair is a modulo, not a clamp. Because the old list was this exact
        /// list repeated, index 61 of a 3x28 list and index 5 of a 28 list are the
        /// same sound -- so `value % Count` recovers the clip the machine was saved
        /// with, where clamping would silently substitute the last one in the menu.
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
        /// Both velocity toggles land here, so the pitch limits follow "either one
        /// is on" rather than whichever was clicked last. The argument is ignored
        /// for that reason.
        /// </summary>
        private void VelocityDep(bool toggleState)
        {
            bool active = VelocityActive;
            MinPitch.DisplayInMapper = active;
            MaxPitch.DisplayInMapper = active;
        }

        /// <summary>
        /// Carries a machine saved by the released 0.1.5 onto the two velocity
        /// toggles that replaced the Velocity toggle and its Translation/Rotation
        /// menu. Runs once, and only while the new toggles are untouched, so a
        /// re-saved machine is never second-guessed.
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
        /// Besiege keeps the machine, and so these behaviours, alive between
        /// simulation runs, and none of this state was ever put back. The burn
        /// effect is the visible case: once SpeedUp had climbed past 1000 the block
        /// stayed silent and PitchFactor stayed wound up for every later run, so a
        /// block that had burned once was dead until the machine was reloaded.
        /// </summary>
        public override void OnSimulateStart()
        {
            hasStarted = false;
            startFrames = 0;
            canPlay = false;
            input = 0f;
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
        /// Writes a repaired index back into the menus themselves, so a machine
        /// saved by 0.1.5 or earlier stops carrying an out-of-range one around.
        /// MenuIndex already copes at every read; doing it here as well is what
        /// makes the mapper show the right entry and makes re-saving the machine
        /// write an index the next load will not have to repair.
        ///
        /// This runs at simulation start rather than in SafeAwake on purpose: the
        /// block's saved data is deserialised into the mapper after SafeAwake, so
        /// there would be nothing to repair yet.
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
                        SpecialSwitcher(SpecialToggle.IsActive);
                    }
                    else if (StatMaster.isHosting && StatMaster.InGlobalPlayMode)
                    {
                        canPlay = true;
                        SpecialSwitcher(SpecialToggle.IsActive);
                    }
                    else if (StatMaster.isClient)
                    {
                        if (StatMaster.InGlobalPlayMode || StatMaster.InLocalPlayMode)
                        {
                            canPlay = true;
                            SpecialSwitcher(SpecialToggle.IsActive);
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

            // Differentiate the block's own position every frame whether or not
            // Translation is on, so switching it on mid-simulation does not read a
            // stale prevPos as one enormous jump. deltaTime is zero at timeScale 0,
            // which would make this infinite.
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
                    // A block has no velocity of its own until it is a rigidbody,
                    // hence differentiating the transform rather than asking.
                    velocityPitch = velocityVector.magnitude / 100f;
                }
                if (Rotation.IsActive)
                {
                    Rigidbody body = gameObject.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        float spin = body.angularVelocity.magnitude / 50f;
                        // With both on, the livelier motion wins. With only one on
                        // this is exactly what the old Translation/Rotation menu did.
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

            input = PlayKey.Value;
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
        private void SpecialSwitcher(bool flag)
        {
            if (SpecialToggle.IsActive)
            {
                // Was Modes[SpecialMenu.Value % Modes.Length], which divides by zero
                // on a block whose XML declares no <CustomMode> at all.
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
                FileChecker(SoundNames[index]);
            }
        }

        private void FileChecker(string SoundName)
        {
            if (SoundNames.Contains(SoundName))
            {
                SoundLoader(SoundName);
            }
            else
            {
                ModConsole.Log("Audio File not found, will play default. ");
            }
        }

        private void SoundLoader(string SoundName)
        {
            try
            {
                audioClip = ModResource.GetAudioClip(SoundName);
                source_audio.clip = audioClip;
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
        /// Burn effect: while the block is on fire the pitch climbs and the volume
        /// falls, until it gives out entirely.
        /// </summary>
        /// <summary>
        /// True while the block is actually alight. Prefers the block's own
        /// FireController, wherever on the block it sits, and keeps the base
        /// property as a fallback so nothing regresses if the handler's own lookup
        /// did find one.
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

        private void TestBroadcast(string text)
        {
            Message message = Messages.Test.CreateMessage(new object[] { text });
            ModNetworking.SendInSimulation(message);
        }
    }
}
