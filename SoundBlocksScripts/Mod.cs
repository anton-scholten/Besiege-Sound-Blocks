using Modding;
using Modding.Modules;
using UnityEngine;

namespace SoundBlocksMod
{
    /// <summary>
    /// Drives the compact mapper layout once a frame, for the whole session.
    ///
    /// On its own object rather than on the block: Unity stops a MonoBehaviour's
    /// coroutines when it is disabled, which is what happens to build-area blocks
    /// during simulation -- a loop hosted on the block died on the first simulate
    /// and never came back.
    /// </summary>
    public class MapperLayoutHost : MonoBehaviour
    {
        private void Update()
        {
            MapperLayout.Tick();
        }
    }

    /// <summary>
    /// Entry point. Registers the block module so a &lt;SoundBlocksMod&gt; element in
    /// a block's XML is deserialised into <see cref="SoundBlocks"/> and driven by
    /// <see cref="SoundBlocksBehaviour"/>.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        public override void OnLoad()
        {
            // The name must match the element in SoundBlock.xml and the [XmlRoot]
            // on SoundBlocks. The bool is "official module": false for a mod.
            CustomModules.AddBlockModule<SoundBlocks, SoundBlocksBehaviour>("SoundBlocksMod", false);

            GameObject host = new GameObject("SoundBlocksMapperLayout");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<MapperLayoutHost>();
        }
    }
}
