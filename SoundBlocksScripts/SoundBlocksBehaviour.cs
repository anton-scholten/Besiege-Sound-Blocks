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

        private Vector3 prevPos;
        private Vector3 velocityVector;
        private MToggle VeloDependent;
        private MValue MinPitch;
        private MValue MaxPitch;
        private MSlider MaxDist;
        private MMenu VeloTraRot;

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
            VeloDependent = AddToggle("Velocity", "VeloDependent", false);
            BurnEffect = AddToggle("Burn effect", "BurnEffect", true);
            SpecialToggle = AddToggle("Special Mode", "SpecialToggle", false);

            MenuHandler();

            MinPitch = AddValue("Min velocity pitch", "MinPitchKey", 0.5f);
            MaxPitch = AddValue("Max velocity pitch", "MaxPitchKey", 1.5f);
            MaxDist = AddSlider("Max Dist", "MaxDistKey", 250f, 0f, 500f);
            VeloTraRot = AddMenu("VeloTraRotKey", 0, new List<string> { "Translation", "Rotation" }, false);

            DistDependent.Toggled += new ToggleHandler(DistanceDep);
            VeloDependent.Toggled += new ToggleHandler(VelocityDep);
            SpecialToggle.Toggled += new ToggleHandler(SpecialMode);
        }

        /// <summary>
        /// Builds the two menus out of what the block's XML declared: every clip in
        /// &lt;Sounds&gt; and every entry in &lt;CustomMode&gt;.
        /// </summary>
        public void MenuHandler()
        {
            // Both lists are marked [CanBeEmpty], so a block XML is allowed to
            // declare neither, and the loader leaves the array null in that case.
            if (Module.Sounds != null)
            {
                foreach (object sound in Module.Sounds)
                {
                    ModAudioClip clip = GetResource((Modding.Serialization.ResourceReference)sound) as ModAudioClip;
                    if (clip != null)
                    {
                        SoundNames.Add(clip.Name);
                    }
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
        /// A menu's saved index is deserialised straight out of the machine file
        /// with no bounds check, and MMenu.Selection indexes its list unchecked --
        /// so a machine saved when the menu was a different length would throw here
        /// rather than in anything that could report it. Returns -1 for an empty
        /// menu, which every caller treats as "nothing to play".
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
            if (value >= items.Count)
            {
                return items.Count - 1;
            }
            return value;
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

        private void VelocityDep(bool toggleState)
        {
            MinPitch.DisplayInMapper = toggleState;
            MaxPitch.DisplayInMapper = toggleState;
            VeloTraRot.DisplayInMapper = toggleState;
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

            if (VeloDependent.IsActive)
            {
                float velocityPitch;
                if (VeloTraRot.isDefaultValue)
                {
                    // Translation: differentiate the block's own position, since a
                    // block has no velocity of its own until it is a rigidbody.
                    velocityVector = (gameObject.transform.position - prevPos) / Time.deltaTime;
                    prevPos = gameObject.transform.position;
                    velocityPitch = velocityVector.magnitude / 100f;
                }
                else
                {
                    // Rotation.
                    Rigidbody body = gameObject.GetComponent<Rigidbody>();
                    velocityPitch = body.angularVelocity.magnitude / 50f;
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
        public override void SimulateFixedUpdateAlways()
        {
            if (BurnEffect.IsActive)
            {
                if (IsBurning || BurningNow)
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
