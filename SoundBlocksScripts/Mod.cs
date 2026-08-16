using System;
using Modding;
using Modding.Common;
using Modding.Modules;
using UnityEngine;

namespace SoundBlocksMod
{
    /// <summary>
    /// Drives the compact mapper layout, one call per frame for the whole session.
    ///
    /// It lives on its own object rather than on the block for a reason: Unity
    /// stops a MonoBehaviour's coroutines when it is disabled, which is what
    /// happens to build-area blocks during simulation, so a loop hosted on the
    /// block died the first time you pressed simulate and never came back.
    /// </summary>
    public class MapperLayoutHost : MonoBehaviour
    {
        private void Update()
        {
            MapperLayout.Tick();
        }
    }

    /// <summary>
    /// Network message types this mod registers. Populated in <see cref="Mod.OnLoad"/>,
    /// because a message type can only be created once the mod is loaded.
    /// </summary>
    public static class Messages
    {
        public static MessageType Test;
    }

    /// <summary>
    /// Entry point. Registers the block module and its behaviour with the game so
    /// that a &lt;SoundBlocksMod&gt; element in a block's XML gets deserialised into
    /// <see cref="SoundBlocks"/> and driven by <see cref="SoundBlocksBehaviour"/>.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        public override void OnLoad()
        {
            // The name here must match the element name in SoundBlock.xml and the
            // [XmlRoot] on SoundBlocks. The bool is "official module": false for a mod.
            CustomModules.AddBlockModule<SoundBlocks, SoundBlocksBehaviour>("SoundBlocksMod", false);

            GameObject host = new GameObject("SoundBlocksMapperLayout");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MapperLayoutHost>();

            Messages.Test = ModNetworking.CreateMessageType(new DataType[] { DataType.String });
            ModNetworking.Callbacks[Messages.Test] += new Action<Message>(TestCallback);
        }

        private void TestCallback(Message msg)
        {
            Player sender = msg.Sender;
            string text = (string)msg.GetData(0);
            ModConsole.Log(sender.Name + ": " + text);
        }
    }
}
