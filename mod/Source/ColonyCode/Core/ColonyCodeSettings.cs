using ColonyCode.World;
using Verse;

namespace ColonyCode.Core
{
    /// <summary>Persistent mod settings (Mods/Config). Read via ColonyCodeMod.Settings.</summary>
    public class ColonyCodeSettings : ModSettings
    {
        public const int DefaultPort = 7878;
        /// <summary>Number of ports tried, starting at <see cref="port"/> (7878..7887 by default).</summary>
        public const int PortRange = 10;

        /// <summary>First port the bridge tries.</summary>
        public int port = DefaultPort;

        /// <summary>Flavor: colony thoughts ("Shipped it!", "Broken build").</summary>
        public bool flavorThoughts = true;

        /// <summary>Flavor: small Intellectual XP for agent pawns while they code.</summary>
        public bool flavorSkillXp = true;

        /// <summary>Flavor: speech motes over agent pawns (agent.say).</summary>
        public bool flavorSpeech = true;

        /// <summary>New games start with a small powered office and its remote workers (GenStep_RemoteOffice).</summary>
        public bool startWithOffice = true;

        /// <summary>Small: 2 desks. Large: 4 desks, a second server rack and a lounge corner.</summary>
        public OfficeSize officeSize = OfficeSize.Small;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref port, "port", DefaultPort);
            Scribe_Values.Look(ref flavorThoughts, "flavorThoughts", true);
            Scribe_Values.Look(ref flavorSkillXp, "flavorSkillXp", true);
            Scribe_Values.Look(ref flavorSpeech, "flavorSpeech", true);
            Scribe_Values.Look(ref startWithOffice, "startWithOffice", true);
            Scribe_Values.Look(ref officeSize, "officeSize", OfficeSize.Small);
            if (port < 1024 || port > 65535 - PortRange) port = DefaultPort;
        }
    }
}
