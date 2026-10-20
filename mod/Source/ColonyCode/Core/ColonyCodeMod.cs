using System;
using ColonyCode.World;
using UnityEngine;
using Verse;

namespace ColonyCode.Core
{
    public class ColonyCodeMod : Mod
    {
        public static ColonyCodeMod Instance { get; private set; }

        /// <summary>Current settings. Never null after the mod is constructed.</summary>
        public static ColonyCodeSettings Settings { get; private set; }

        private string portBuffer;

        public ColonyCodeMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<ColonyCodeSettings>();
            try { BridgeHost.SetModVersion(content?.ModMetaData?.ModVersion); } catch { }
            try
            {
                BridgeHost.Start(Settings.port);
            }
            catch (Exception e)
            {
                Log.Error("[ColonyCode] Could not start bridge: " + e);
            }
        }

        public override string SettingsCategory()
        {
            return "Colony Code";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("Bridge: " + BridgeHost.StatusText);
            var s = BridgeHost.Server;
            if (s != null && s.IsRunning)
            {
                list.Label("Token file: " + s.BridgeFilePath);
                list.Label("Requests served: " + s.RequestCount + "   Queued events: " + s.PendingEventCount +
                           "   Undelivered inbox items: " + s.PendingInboxCount);
            }
            else
            {
                var err = s?.LastError;
                if (!string.IsNullOrEmpty(err)) list.Label("Last error: " + err);
            }
            list.GapLine(12f);

            if (portBuffer == null) portBuffer = Settings.port.ToString();
            list.TextFieldNumericLabeled("First port (tries " + ColonyCodeSettings.PortRange + " ports from here)",
                ref Settings.port, ref portBuffer, 1024, 65535 - ColonyCodeSettings.PortRange);
            if (list.ButtonText("Restart bridge"))
            {
                BridgeHost.Restart(Settings.port);
            }
            list.GapLine(12f);

            list.Label("Flavor (cosmetic, no other gameplay effect):");
            list.CheckboxLabeled("Colony thoughts when goals finish or tests fail", ref Settings.flavorThoughts);
            list.CheckboxLabeled("Intellectual XP for agent pawns", ref Settings.flavorSkillXp);
            list.CheckboxLabeled("Speech bubbles over agent pawns", ref Settings.flavorSpeech);
            list.GapLine(12f);

            list.Label("ColonyCode_SettingsOfficeHeader".Translate());
            list.CheckboxLabeled("ColonyCode_SettingsOfficeToggle".Translate(), ref Settings.startWithOffice,
                "ColonyCode_SettingsOfficeToggleDesc".Translate());
            if (Settings.startWithOffice)
            {
                if (list.RadioButton("ColonyCode_SettingsOfficeSmall".Translate(), Settings.officeSize == OfficeSize.Small, 12f))
                    Settings.officeSize = OfficeSize.Small;
                if (list.RadioButton("ColonyCode_SettingsOfficeLarge".Translate(), Settings.officeSize == OfficeSize.Large, 12f))
                    Settings.officeSize = OfficeSize.Large;
            }

            list.End();
        }
    }
}
