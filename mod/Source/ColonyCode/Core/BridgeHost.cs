using System;
using System.Collections.Generic;
using ColonyCode.Http;
using RimWorld;
using Verse;

namespace ColonyCode.Core
{
    /// <summary>
    /// Owns the single BridgeServer for the whole game process. Started once when the mod loads,
    /// stopped when the application quits (UnityEngine.Application.quitting).
    /// </summary>
    public static class BridgeHost
    {
        public const string FallbackModVersion = "0.1.0";

        private static readonly object gate = new object();
        private static BridgeServer server;
        private static bool quitHooked;
        private static string modVersion = FallbackModVersion;

        /// <summary>The running server, or null if it failed to start / is stopped.</summary>
        public static BridgeServer Server => server;

        public static bool IsRunning => server != null && server.IsRunning;

        public static string ModVersion => modVersion;

        public static void SetModVersion(string v)
        {
            if (!string.IsNullOrEmpty(v)) modVersion = v;
        }

        /// <summary>Starts the bridge on [firstPort, firstPort + PortRange - 1]. No-op if already running.</summary>
        public static void Start(int firstPort)
        {
            Start(firstPort, null);
        }

        private static void Start(int firstPort, BridgeServer previous)
        {
            lock (gate)
            {
                if (server != null && server.IsRunning) return;
                if (!quitHooked)
                {
                    UnityEngine.Application.quitting += OnQuitting;
                    // 1.7 runs on .NET 10 with Unity stand-ins whose quitting event never fires; the
                    // process-exit event does on both runtimes, so bridge.json is removed either way.
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => OnQuitting();
                    quitHooked = true;
                }
                string game;
                try { game = VersionControl.CurrentVersionStringWithRev; } catch { game = "unknown"; }
                var s = new BridgeServer(game, modVersion, firstPort, firstPort + ColonyCodeSettings.PortRange - 1);
                // Decisions only count as open while a loaded game can show them (see GameComponent_ColonyCode).
                s.RequireHeartbeat = true;
                // macOS throttles RimWorld to near zero frames while another app has focus, so a few
                // seconds without a frame does not mean the game is gone; only a long silence does.
                s.HeartbeatTimeoutSeconds = 90;
                if (previous != null) s.AdoptStateFrom(previous);
                bool ok;
                try { ok = s.Start(); }
                catch (Exception e) { ok = false; Log.Error("[ColonyCode] Bridge failed to start: " + e); }
                FlushLogs(s);
                if (ok)
                {
                    server = s;
                    Log.Message("[ColonyCode] Bridge listening on 127.0.0.1:" + s.Port + " (token file " + s.BridgeFilePath + ")");
                }
                else
                {
                    server = null;
                    Log.Warning("[ColonyCode] Bridge not started: " + (s.LastError ?? "unknown error"));
                }
            }
        }

        public static void Stop()
        {
            lock (gate)
            {
                var s = server;
                server = null;
                if (s == null) return;
                try { s.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Stops the listener and starts a new one, carrying over the token, undrained events,
        /// undelivered inbox items and decisions, so a connected plugin loses nothing.
        /// </summary>
        public static void Restart(int firstPort)
        {
            BridgeServer old;
            lock (gate) old = server;
            Stop();
            Start(firstPort, old);
        }

        /// <summary>One-line status for UI, e.g. "Listening on 127.0.0.1:7878, plugin connected".</summary>
        public static string StatusText
        {
            get
            {
                var s = server;
                if (s == null || !s.IsRunning) return "Bridge stopped";
                string client = s.HasRecentClient(TimeSpan.FromSeconds(40))
                    ? "Claude Code connected"
                    : "waiting for Claude Code";
                return "Listening on 127.0.0.1:" + s.Port + " - " + client;
            }
        }

        /// <summary>Main thread: move the server's background log lines into the RimWorld log.</summary>
        public static void FlushLogs(BridgeServer s = null)
        {
            s = s ?? server;
            if (s == null) return;
            string line;
            int n = 0;
            while (n++ < 50 && s.TryDequeueLog(out line))
            {
                if (Prefs.DevMode) Log.Message(line);
            }
        }

        private static void OnQuitting()
        {
            Stop();
        }
    }

    /// <summary>
    /// Gameplay-facing accessor. Bridge.Instance is null when the bridge is not running; the helpers
    /// below are null-safe and return false/null in that case. All are thread-safe.
    /// </summary>
    public static class Bridge
    {
        public static BridgeServer Instance => BridgeHost.Server;

        public static bool IsRunning => BridgeHost.IsRunning;

        /// <summary>Queue an inbox item { type, sessionId?, ...payload } for the plugin. Returns its id or null.</summary>
        public static string EnqueueInbox(string type, Dictionary<string, object> payload = null, string sessionId = null)
        {
            var s = Instance;
            return s == null ? null : s.EnqueueInbox(type, payload, sessionId);
        }

        /// <summary>Record the player's answer for decision id (option is one of the decision's options).</summary>
        public static bool AnswerDecision(string id, string option, string text = null)
        {
            var s = Instance;
            return s != null && s.AnswerDecision(id, option, text);
        }

        public static void RegisterDecision(string id)
        {
            Instance?.RegisterDecision(id);
        }

        public static void ForgetDecision(string id)
        {
            Instance?.ForgetDecision(id);
        }
    }
}
