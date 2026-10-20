using System;
using System.Collections.Generic;
using Verse;

namespace ColonyCode.Core
{
    /// <summary>
    /// Drains bridge events on the main thread every frame (also while paused) and raises
    /// <see cref="OnBridgeEvent"/> for each. RimWorld adds this component to every game automatically.
    /// </summary>
    public class GameComponent_ColonyCode : GameComponent
    {
        /// <summary>Max events dispatched per frame; the rest wait for the next frame.</summary>
        public const int MaxEventsPerFrame = 500;

        /// <summary>
        /// Raised on the main thread for every event the plugin POSTs to /events, as parsed JSON
        /// (Dictionary&lt;string,object&gt; with "type", "ts", "sessionId", ...payload). Static: subscribe
        /// once (e.g. from a [StaticConstructorOnStartup] class); it survives game loads. Only fires
        /// while a game is loaded. A throwing handler is logged and does not affect other handlers.
        /// </summary>
        public static event Action<Dictionary<string, object>> OnBridgeEvent;

        /// <summary>The component of the current game, or null at the main menu.</summary>
        public static GameComponent_ColonyCode Instance { get; private set; }

        public GameComponent_ColonyCode() { Instance = this; }

        public GameComponent_ColonyCode(Game game) { Instance = this; }

        public override void FinalizeInit()
        {
            // Decisions belong to the game that showed them; a newly started or loaded game has none.
            BridgeHost.Server?.ForgetAllDecisions();
        }

        public override void GameComponentUpdate()
        {
            Instance = this;
            BridgeHost.FlushLogs();
            var server = BridgeHost.Server;
            if (server == null) return;
            // Tells the bridge a game is on screen that can show decisions (see BridgeServer.RequireHeartbeat).
            server.Heartbeat();

            int n = 0;
            Dictionary<string, object> ev;
            while (n < MaxEventsPerFrame && server.TryDequeueEvent(out ev))
            {
                n++;
                Dispatch(ev);
            }
        }

        private static void Dispatch(Dictionary<string, object> ev)
        {
            var handlers = OnBridgeEvent;
            if (handlers == null) return;
            foreach (Action<Dictionary<string, object>> h in handlers.GetInvocationList())
            {
                try
                {
                    h(ev);
                }
                catch (Exception e)
                {
                    object type;
                    ev.TryGetValue("type", out type);
                    Log.ErrorOnce("[ColonyCode] Bridge event handler failed for '" + type + "': " + e,
                        (h.Method.Name + type).GetHashCode());
                }
            }
        }
    }
}
