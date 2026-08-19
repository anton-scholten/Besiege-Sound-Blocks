using System;
using System.Xml.Serialization;
using Modding.Modules;
using Modding.Serialization;

namespace SoundBlocksMod
{
    /// <summary>
    /// The block module: the deserialised form of the &lt;SoundBlocksMod&gt; element
    /// in SoundBlock.xml. The loader fills these fields in before the behaviour's
    /// SafeAwake runs, so the behaviour can read them to build its mapper menus.
    ///
    /// System.Xml is on the mod loader's blacklist, but only as *code*: its
    /// AssemblyScanner walks field types, locals and IL operands and never looks at
    /// custom attributes, so these [Xml*] markers are the supported way to name the
    /// elements a module deserialises.
    /// </summary>
    [XmlRoot("SoundBlocksMod")]
    public class SoundBlocks : BlockModule
    {
        /// <summary>
        /// The clips offered in the block's sound menu. Declared as object[] because
        /// the loader hands back ResourceReference instances that the behaviour
        /// resolves through GetResource.
        /// </summary>
        [XmlArray("Sounds")]
        [XmlArrayItem("AudioClip", typeof(ResourceReference))]
        [RequireToValidate]
        [CanBeEmpty]
        public object[] Sounds;

        /// <summary>Three-clip start/loop/stop sets offered in Special Mode.</summary>
        [XmlArray("CustomMode")]
        [XmlArrayItem("Mode")]
        [RequireToValidate]
        [CanBeEmpty]
        public Mode[] Modes;
    }

    /// <summary>
    /// One Special Mode entry: a start clip, a looping clip and a stop clip, played
    /// in that order as the key is pressed, held and released.
    /// </summary>
    [Serializable]
    public class Mode : Element
    {
        [XmlAttribute("name")]
        public string Name;

        [XmlAttribute("Sound_1")]
        public string Sound_1;

        [XmlAttribute("Sound_2")]
        public string Sound_2;

        [XmlAttribute("Sound_3")]
        public string Sound_3;
    }
}
