using System;
using System.Collections.Generic;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>A goal that reached done or failed. Saved with the game.</summary>
    public class JournalEntry : IExposable
    {
        public string goalId;
        public string text;
        public string summary;
        /// <summary>"done" | "failed"</summary>
        public string status;
        public string sessionId;
        public string sessionLabel;
        /// <summary>Names of the agents involved, as shown when the goal finished.</summary>
        public List<string> agents = new List<string>();
        /// <summary>Find.TickManager.TicksAbs at start / end; -1 when unknown.</summary>
        public long startTicksAbs = -1;
        public long endTicksAbs = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref goalId, "goalId");
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref summary, "summary");
            Scribe_Values.Look(ref status, "status");
            Scribe_Values.Look(ref sessionId, "sessionId");
            Scribe_Values.Look(ref sessionLabel, "sessionLabel");
            Scribe_Collections.Look(ref agents, "agents", LookMode.Value);
            Scribe_Values.Look(ref startTicksAbs, "startTicksAbs", -1L);
            Scribe_Values.Look(ref endTicksAbs, "endTicksAbs", -1L);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && agents == null) agents = new List<string>();
        }
    }

    /// <summary>Journal half of the registry: finished goals, saved in the save file (newest last, capped).</summary>
    public partial class AgentRegistry
    {
        public const int MaxJournalEntries = 200;

        /// <summary>A journal entry was added or updated.</summary>
        public static event Action<JournalEntry> JournalChanged;

        private List<JournalEntry> journal = new List<JournalEntry>();

        /// <summary>Finished goals, oldest first.</summary>
        public IReadOnlyList<JournalEntry> Journal => journal;

        private void ExposeJournal()
        {
            Scribe_Collections.Look(ref journal, "journal", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (journal == null) journal = new List<JournalEntry>();
                journal.RemoveAll(j => j == null);
                if (journal.Count > MaxJournalEntries) journal.RemoveRange(0, journal.Count - MaxJournalEntries);
            }
        }

        /// <summary>The goal is active: its session's lead and currently busy agents are involved.</summary>
        private void NoteGoalActive(GoalInfo g)
        {
            if (string.IsNullOrEmpty(g.sessionId)) return;
            foreach (var a in agents)
                if (a.sessionId == g.sessionId && (a.role == "lead" || IsActive(a.state))) g.agentIds.Add(a.id);
        }

        /// <summary>A busy agent is involved in every active goal of its session.</summary>
        private void NoteAgentBusy(AgentInfo a)
        {
            if (string.IsNullOrEmpty(a.sessionId)) return;
            foreach (var g in goals)
                if (g.status == "active" && g.sessionId == a.sessionId) g.agentIds.Add(a.id);
        }

        private void RecordJournal(GoalInfo g)
        {
            var e = journal.Find(x => x.goalId == g.id);
            bool isNew = e == null;
            if (isNew) e = new JournalEntry { goalId = g.id };
            e.text = Clip(g.text, 2000) ?? ("#" + g.id);
            e.summary = Clip(g.summary, 4000);
            e.status = g.status;
            e.sessionId = g.sessionId;
            e.sessionLabel = string.IsNullOrEmpty(g.sessionId) ? null : SessionLabel(g.sessionId);
            e.startTicksAbs = g.startTicksAbs;
            e.endTicksAbs = NowTicksAbs();
            e.agents.Clear();
            var ids = new List<string>(g.agentIds);
            if (ids.Count == 0 && !string.IsNullOrEmpty(g.sessionId)) ids.Add(LeadId(g.sessionId));
            foreach (var id in ids)
            {
                var a = Get(id);
                string name = a != null ? (a.displayName ?? a.label ?? a.id) : id;
                if (!e.agents.Contains(name)) e.agents.Add(name);
            }
            if (isNew)
            {
                journal.Add(e);
                if (journal.Count > MaxJournalEntries) journal.RemoveRange(0, journal.Count - MaxJournalEntries);
            }
            var handlers = JournalChanged;
            if (handlers == null) return;
            foreach (Action<JournalEntry> h in handlers.GetInvocationList())
            {
                try { h(e); }
                catch (Exception ex) { Log.Error("[ColonyCode] JournalChanged handler failed: " + ex); }
            }
        }
    }
}
