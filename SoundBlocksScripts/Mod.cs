using System;
using Modding;
using Modding.Common;
using Modding.Modules;

namespace SoundBlocksMod
{
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
