// Pure .NET. No Verse / Unity references: this file is unit-tested under plain mono.
// Implements the Colony Code bridge protocol v0, server side.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ColonyCode.Http
{
    /// <summary>
    /// Localhost HTTP bridge between the Claude Code plugin (client) and the game (server).
    ///
    /// Threading: the listener and request handlers run on background threads. They never touch
    /// game state. Incoming events are queued and must be drained by the game's main thread with
    /// <see cref="TryDequeueEvent"/>. The game pushes inbox items with <see cref="EnqueueInbox(Dictionary{string,object})"/>
    /// and decision answers with <see cref="AnswerDecision"/>; both are safe from any thread.
    /// </summary>
    public sealed class BridgeServer : IDisposable
    {
        public const int ProtocolVersion = 0;
        public const int DefaultPort = 7878;
        public const int DefaultLastPort = 7887;
        public const int MaxWaitSeconds = 30;
        public const int DefaultWaitSeconds = 25;
        public const string TokenHeader = "X-Colony-Token";
        public const string ChallengeHeader = "X-Colony-Challenge";

        // ---- configuration (set before Start) ----
        public int MaxConcurrentRequests = 32;
        public int MaxQueuedEvents = 10000;
        public int MaxBodyBytes = 4 * 1024 * 1024;
        public int MaxDecisions = 2000;
        public int MaxInboxItems = 1000;

        /// <summary>
        /// When true, decisions count as "open" only while the game's main thread calls
        /// <see cref="Heartbeat"/> (i.e. a game is loaded and can show the question). Without a recent
        /// heartbeat, decision.open is not registered early and polls of unanswered decisions return
        /// "unknown", so the plugin falls back to the terminal prompt at once.
        /// </summary>
        public bool RequireHeartbeat;
        public double HeartbeatTimeoutSeconds = 5;
        private long heartbeatTicks;
        private string adoptedToken;

        private readonly string gameVersion;
        private readonly string modVersion;
        private readonly int firstPort;
        private readonly int lastPort;
        private readonly string stateDir;

        private HttpListener listener;
        private Thread listenerThread;
        private volatile bool running;
        private int activeRequests;
        private long requestCount;
        private long lastRequestTicks; // DateTime.UtcNow.Ticks of last authorized request
        private long itemCounter;

        private readonly ConcurrentQueue<Dictionary<string, object>> events = new ConcurrentQueue<Dictionary<string, object>>();
        private readonly ConcurrentQueue<string> logs = new ConcurrentQueue<string>();

        private readonly object inboxLock = new object();
        private readonly LinkedList<Dictionary<string, object>> inbox = new LinkedList<Dictionary<string, object>>();

        private readonly object decisionLock = new object();
        private readonly Dictionary<string, DecisionState> decisions = new Dictionary<string, DecisionState>();
        private readonly LinkedList<string> decisionOrder = new LinkedList<string>();

        private sealed class DecisionState
        {
            public bool Answered;
            public string Option;
            public string Text;
        }

        /// <param name="gameVersion">Reported in /health and bridge.json, e.g. "1.6.4871 rev595".</param>
        /// <param name="modVersion">Reported in /health and bridge.json, e.g. "0.1.0".</param>
        /// <param name="firstPort">First port to try (default 7878).</param>
        /// <param name="lastPort">Last port to try, inclusive (default 7887).</param>
        /// <param name="stateDir">Directory for bridge.json. Default: $HOME/.colony-code (USERPROFILE on Windows).</param>
        public BridgeServer(string gameVersion, string modVersion, int firstPort = DefaultPort, int lastPort = DefaultLastPort, string stateDir = null)
        {
            this.gameVersion = gameVersion ?? "unknown";
            this.modVersion = modVersion ?? "unknown";
            this.firstPort = firstPort;
            this.lastPort = Math.Max(firstPort, lastPort);
            this.stateDir = stateDir ?? DefaultStateDir();
        }

        // ------------------------------------------------------------ status

        public bool IsRunning => running;
        /// <summary>Port actually bound, or 0 when not running.</summary>
        public int Port { get; private set; }
        /// <summary>32 hex chars; regenerated on each Start.</summary>
        public string Token { get; private set; }
        /// <summary>Last start/stop error, or null.</summary>
        public string LastError { get; private set; }
        public string BridgeFilePath => Path.Combine(stateDir, "bridge.json");
        public long RequestCount => Interlocked.Read(ref requestCount);
        public int PendingEventCount => events.Count;

        /// <summary>UTC time of the last authorized request, or DateTime.MinValue.</summary>
        public DateTime LastRequestUtc
        {
            get
            {
                long t = Interlocked.Read(ref lastRequestTicks);
                return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
            }
        }

        /// <summary>True if an authorized request arrived within the window (a plugin is polling).</summary>
        public bool HasRecentClient(TimeSpan window)
        {
            long t = Interlocked.Read(ref lastRequestTicks);
            return t != 0 && DateTime.UtcNow.Ticks - t <= window.Ticks;
        }

        // ------------------------------------------------------------ lifecycle

        /// <summary>Binds the first free port in [firstPort, lastPort], writes bridge.json. Returns false on failure (see LastError).</summary>
        public bool Start()
        {
            if (running) return true;
            LastError = null;
            Token = adoptedToken ?? NewToken();
            adoptedToken = null;
            for (int port = firstPort; port <= lastPort; port++)
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                try
                {
                    l.Start();
                }
                catch (Exception e)
                {
                    try { l.Close(); } catch { }
                    Log("port " + port + " unavailable: " + e.Message);
                    LastError = "no free port in " + firstPort + ".." + lastPort + " (last: " + e.Message + ")";
                    continue;
                }
                listener = l;
                Port = port;
                running = true;
                LastError = null;
                break;
            }
            if (!running) { Port = 0; return false; }

            listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "ColonyCode.Bridge.Listener" };
            listenerThread.Start();

            try { WriteBridgeFile(); }
            catch (Exception e)
            {
                LastError = "could not write " + BridgeFilePath + ": " + e.Message;
                Log(LastError);
            }
            Log("listening on 127.0.0.1:" + Port);
            return true;
        }

        /// <summary>Stops listening, releases long-polls, deletes bridge.json. Safe to call repeatedly.</summary>
        public void Stop()
        {
            if (!running && listener == null) return;
            running = false;
            lock (inboxLock) Monitor.PulseAll(inboxLock);
            lock (decisionLock) Monitor.PulseAll(decisionLock);
            var l = listener;
            listener = null;
            if (l != null)
            {
                try { l.Stop(); } catch { }
                try { l.Close(); } catch { }
            }
            var t = listenerThread;
            listenerThread = null;
            if (t != null && t != Thread.CurrentThread)
            {
                // Closing the listener makes GetContext throw at once; keep the main-thread wait short.
                try { t.Join(500); } catch { }
            }
            try { DeleteBridgeFile(); } catch (Exception e) { Log("could not delete bridge file: " + e.Message); }
            Log("stopped (port " + Port + ")");
            Port = 0;
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// Call before <see cref="Start"/> on a fresh server that replaces a stopped one (settings
        /// "Restart bridge"): takes over its token, undrained events, undelivered inbox items and
        /// decisions, so nothing in flight is lost and a connected plugin keeps its token.
        /// </summary>
        public void AdoptStateFrom(BridgeServer old)
        {
            if (old == null || old == this || running) return;
            adoptedToken = old.Token;
            Interlocked.Exchange(ref heartbeatTicks, Interlocked.Read(ref old.heartbeatTicks));
            Dictionary<string, object> ev;
            while (old.events.TryDequeue(out ev)) events.Enqueue(ev);
            lock (old.inboxLock)
            lock (inboxLock)
            {
                foreach (var item in old.inbox) inbox.AddLast(item);
                old.inbox.Clear();
            }
            lock (old.decisionLock)
            lock (decisionLock)
            {
                foreach (var id in old.decisionOrder)
                {
                    DecisionState st;
                    if (decisions.ContainsKey(id) || !old.decisions.TryGetValue(id, out st)) continue;
                    decisions[id] = st;
                    decisionOrder.AddLast(id);
                }
            }
        }

        /// <summary>Main thread, every frame while a game is loaded: marks the game as able to show decisions.</summary>
        public void Heartbeat()
        {
            Interlocked.Exchange(ref heartbeatTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>True when heartbeats are not required, or one arrived within HeartbeatTimeoutSeconds.</summary>
        public bool GameAlive
        {
            get
            {
                if (!RequireHeartbeat) return true;
                long t = Interlocked.Read(ref heartbeatTicks);
                return t != 0 && DateTime.UtcNow.Ticks - t <= TimeSpan.FromSeconds(HeartbeatTimeoutSeconds).Ticks;
            }
        }

        /// <summary>Forget every decision (a different game was loaded; its letters are gone).</summary>
        public void ForgetAllDecisions()
        {
            lock (decisionLock)
            {
                decisions.Clear();
                decisionOrder.Clear();
                Monitor.PulseAll(decisionLock);
            }
        }

        /// <summary>Hex HMAC-SHA256(key = UTF-8 token, message = UTF-8 challenge): proves this server holds the token.</summary>
        public static string ComputeProof(string token, string challenge)
        {
            using (var h = new HMACSHA256(new UTF8Encoding(false).GetBytes(token ?? "")))
            {
                byte[] mac = h.ComputeHash(new UTF8Encoding(false).GetBytes(challenge ?? ""));
                var sb = new StringBuilder(64);
                foreach (byte b in mac) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ game-facing API

        /// <summary>Main thread: take the next event posted by the plugin. Each is a parsed JSON object.</summary>
        public bool TryDequeueEvent(out Dictionary<string, object> ev)
        {
            return events.TryDequeue(out ev);
        }

        /// <summary>Main thread: take the next diagnostic log line produced by background threads.</summary>
        public bool TryDequeueLog(out string line)
        {
            return logs.TryDequeue(out line);
        }

        /// <summary>
        /// Queue an inbox item for the plugin. The item must contain "type" plus its payload
        /// (and optionally "sessionId"). "id" and "ts" are filled in when missing. Returns the id.
        /// Thread-safe. Wakes any waiting /inbox long-poll.
        /// </summary>
        public string EnqueueInbox(Dictionary<string, object> item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (!(item.TryGetValue("type", out var t) && t is string)) throw new ArgumentException("inbox item needs a string \"type\"");
            if (!(item.TryGetValue("id", out var id) && id is string idStr && idStr.Length > 0))
            {
                idStr = "in-" + Json.NowMs().ToString(CultureInfo.InvariantCulture) + "-" +
                        Interlocked.Increment(ref itemCounter).ToString(CultureInfo.InvariantCulture);
                item["id"] = idStr;
            }
            if (!item.ContainsKey("ts")) item["ts"] = Json.NowMs();
            lock (inboxLock)
            {
                inbox.AddLast(item);
                while (inbox.Count > MaxInboxItems) inbox.RemoveFirst();
                Monitor.PulseAll(inboxLock);
            }
            return idStr;
        }

        /// <summary>Convenience overload: builds { type, sessionId?, ...payload } and queues it. Returns the id.</summary>
        public string EnqueueInbox(string type, Dictionary<string, object> payload = null, string sessionId = null)
        {
            var item = new Dictionary<string, object>();
            if (payload != null) foreach (var kv in payload) item[kv.Key] = kv.Value;
            item["type"] = type;
            if (!string.IsNullOrEmpty(sessionId)) item["sessionId"] = sessionId;
            return EnqueueInbox(item);
        }

        /// <summary>Number of inbox items not yet delivered.</summary>
        public int PendingInboxCount { get { lock (inboxLock) return inbox.Count; } }

        /// <summary>
        /// Make a decision id known (status "open"). The server also does this automatically when a
        /// decision.open event is POSTed, before the event reaches the main thread. Thread-safe.
        /// </summary>
        public void RegisterDecision(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (decisionLock)
            {
                if (decisions.ContainsKey(id)) return;
                decisions[id] = new DecisionState();
                decisionOrder.AddLast(id);
                while (decisions.Count > MaxDecisions && decisionOrder.First != null)
                {
                    decisions.Remove(decisionOrder.First.Value);
                    decisionOrder.RemoveFirst();
                }
            }
        }

        /// <summary>
        /// Record the player's answer. The next (or a waiting) GET /decision/&lt;id&gt; returns
        /// { status: "answered", option, text? }. Registers the id if it was unknown.
        /// Returns false if the decision was already answered (the first answer wins). Thread-safe.
        /// </summary>
        public bool AnswerDecision(string id, string option, string text = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            RegisterDecision(id);
            lock (decisionLock)
            {
                DecisionState st;
                if (!decisions.TryGetValue(id, out st)) return false;
                if (st.Answered) return false;
                st.Answered = true;
                st.Option = option ?? "";
                st.Text = string.IsNullOrEmpty(text) ? null : text;
                Monitor.PulseAll(decisionLock);
                return true;
            }
        }

        /// <summary>Forget a decision; polls then return "unknown". Thread-safe.</summary>
        public void ForgetDecision(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (decisionLock)
            {
                if (decisions.Remove(id)) decisionOrder.Remove(id);
                Monitor.PulseAll(decisionLock);
            }
        }

        /// <summary>"open", "answered" or "unknown".</summary>
        public string GetDecisionStatus(string id)
        {
            lock (decisionLock)
            {
                DecisionState st;
                if (id == null || !decisions.TryGetValue(id, out st)) return "unknown";
                return st.Answered ? "answered" : "open";
            }
        }

        // ------------------------------------------------------------ listener

        private void ListenLoop()
        {
            var l = listener;
            while (running && l != null)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = l.GetContext();
                }
                catch (Exception e)
                {
                    if (running) { Log("listener error: " + e.Message); Thread.Sleep(50); continue; }
                    break;
                }
                if (Interlocked.Increment(ref activeRequests) > MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref activeRequests);
                    SafeRespond(ctx, 503, Error("busy"));
                    continue;
                }
                try
                {
                    var th = new Thread(() =>
                    {
                        try { Handle(ctx); }
                        catch (Exception e) { Log("handler error: " + e); SafeRespond(ctx, 500, Error("internal error")); }
                        finally { Interlocked.Decrement(ref activeRequests); }
                    }) { IsBackground = true, Name = "ColonyCode.Bridge.Request" };
                    th.Start();
                }
                catch (Exception e)
                {
                    Interlocked.Decrement(ref activeRequests);
                    Log("could not start handler thread: " + e.Message);
                    SafeRespond(ctx, 503, Error("busy"));
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;

            // A browser page always sends Origin on cross-origin requests: refuse them outright.
            if (req.Headers["Origin"] != null)
            {
                SafeRespond(ctx, 403, Error("origin not allowed"));
                return;
            }
            string path = req.Url.AbsolutePath;
            string method = req.HttpMethod;
            string tokenGiven = req.Headers[TokenHeader];
            string challenge = req.Headers[ChallengeHeader];
            bool challengeOk = IsValidChallenge(challenge);

            // The one request allowed without the token: GET /health carrying a challenge, so a client
            // can check that this server holds the token before it sends the token anywhere.
            bool tokenlessChallenge = tokenGiven == null && challengeOk && path == "/health" && method == "GET";
            if (!tokenlessChallenge && !TokenMatches(tokenGiven))
            {
                SafeRespond(ctx, 401, Error("missing or bad " + TokenHeader));
                return;
            }
            if (!tokenlessChallenge)
            {
                Interlocked.Increment(ref requestCount);
                Interlocked.Exchange(ref lastRequestTicks, DateTime.UtcNow.Ticks);
            }

            if (path == "/health")
            {
                if (method != "GET") { MethodNotAllowed(ctx, "GET"); return; }
                var health = new Dictionary<string, object>
                {
                    { "ok", true }, { "protocol", ProtocolVersion }, { "game", gameVersion }, { "mod", modVersion },
                };
                if (challengeOk) health["proof"] = ComputeProof(Token, challenge);
                SafeRespond(ctx, 200, Json.Serialize(health));
                return;
            }
            if (path == "/events")
            {
                if (method != "POST") { MethodNotAllowed(ctx, "POST"); return; }
                HandleEvents(ctx);
                return;
            }
            if (path == "/inbox")
            {
                if (method != "GET") { MethodNotAllowed(ctx, "GET"); return; }
                HandleInbox(ctx);
                return;
            }
            if (path.StartsWith("/decision/", StringComparison.Ordinal) && path.Length > "/decision/".Length)
            {
                string rawId = path.Substring("/decision/".Length);
                if (rawId.IndexOf('/') >= 0) { SafeRespond(ctx, 404, Error("not found")); return; }
                if (method != "GET") { MethodNotAllowed(ctx, "GET"); return; }
                string id;
                try { id = Uri.UnescapeDataString(rawId); } catch { id = rawId; }
                HandleDecision(ctx, id);
                return;
            }
            SafeRespond(ctx, 404, Error("not found"));
        }

        private void HandleEvents(HttpListenerContext ctx)
        {
            string body;
            if (!TryReadBody(ctx.Request, out body))
            {
                SafeRespond(ctx, 413, Error("body too large"));
                return;
            }
            object parsed;
            if (!Json.TryParse(body, out parsed) || !(parsed is List<object> list))
            {
                SafeRespond(ctx, 400, Error("body must be a JSON array of events"));
                return;
            }
            int accepted = 0;
            foreach (object o in list)
            {
                if (!(o is Dictionary<string, object> ev)) continue;
                if (Json.GetString(ev, "type") == "decision.open" && GameAlive)
                {
                    // Register now so an immediate /decision poll sees "open", not "unknown". Only when
                    // a game is running to show it; otherwise AgentRegistry registers it once drained.
                    RegisterDecision(Json.GetString(Json.GetObject(ev, "decision"), "id"));
                }
                events.Enqueue(ev);
                accepted++;
            }
            int dropped = 0;
            while (events.Count > MaxQueuedEvents && events.TryDequeue(out _)) dropped++;
            if (dropped > 0) Log("event queue full; dropped " + dropped + " oldest events");
            SafeRespond(ctx, 200, Json.Serialize(new Dictionary<string, object> { { "ok", true }, { "accepted", accepted } }));
        }

        private void HandleInbox(HttpListenerContext ctx)
        {
            int waitMs = ParseWaitMs(ctx.Request.QueryString["wait"]);
            // Optional ?session=<id>: only items addressed to that session or to no session.
            string session = ctx.Request.QueryString["session"];
            if (string.IsNullOrEmpty(session)) session = null;
            var batch = new List<Dictionary<string, object>>();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            lock (inboxLock)
            {
                while (!AnyInboxFor(session) && running)
                {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) break;
                    Monitor.Wait(inboxLock, remaining);
                }
                var node = inbox.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (InboxItemFor(node.Value, session))
                    {
                        batch.Add(node.Value);
                        inbox.Remove(node);
                    }
                    node = next;
                }
            }
            if (!SafeRespond(ctx, 200, Json.Serialize(batch)) && batch.Count > 0)
            {
                // Client went away before receiving them: put the items back, in order, for the next poll.
                lock (inboxLock)
                {
                    for (int i = batch.Count - 1; i >= 0; i--) inbox.AddFirst(batch[i]);
                    Monitor.PulseAll(inboxLock);
                }
            }
        }

        private static bool InboxItemFor(Dictionary<string, object> item, string session)
        {
            if (session == null) return true;
            object v;
            return !(item.TryGetValue("sessionId", out v) && v is string s && s.Length > 0 && s != session);
        }

        private bool AnyInboxFor(string session)
        {
            foreach (var item in inbox) if (InboxItemFor(item, session)) return true;
            return false;
        }

        private static bool IsValidChallenge(string c)
        {
            if (c == null || c.Length < 16 || c.Length > 128) return false;
            foreach (char ch in c)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private void HandleDecision(HttpListenerContext ctx, string id)
        {
            int waitMs = ParseWaitMs(ctx.Request.QueryString["wait"]);
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            var answer = new Dictionary<string, object>();
            lock (decisionLock)
            {
                while (true)
                {
                    DecisionState st;
                    if (!decisions.TryGetValue(id, out st))
                    {
                        answer["status"] = "unknown";
                        break;
                    }
                    if (st.Answered)
                    {
                        answer["status"] = "answered";
                        answer["option"] = st.Option;
                        if (st.Text != null) answer["text"] = st.Text;
                        break;
                    }
                    if (!GameAlive)
                    {
                        // No game on screen to ask (main menu, loading, frozen): let the terminal ask.
                        answer["status"] = "unknown";
                        break;
                    }
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0 || !running)
                    {
                        answer["status"] = "open";
                        break;
                    }
                    // Wake periodically to notice a stopped heartbeat.
                    Monitor.Wait(decisionLock, RequireHeartbeat ? Math.Min(remaining, 500) : remaining);
                }
            }
            SafeRespond(ctx, 200, Json.Serialize(answer));
        }

        // ------------------------------------------------------------ helpers

        private static int ParseWaitMs(string raw)
        {
            double s = DefaultWaitSeconds;
            if (!string.IsNullOrEmpty(raw))
            {
                double parsed;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !double.IsNaN(parsed))
                    s = parsed;
            }
            if (s < 0) s = 0;
            if (s > MaxWaitSeconds) s = MaxWaitSeconds;
            return (int)(s * 1000);
        }

        private bool TryReadBody(HttpListenerRequest req, out string body)
        {
            body = "";
            if (req.ContentLength64 > MaxBodyBytes) return false;
            if (!req.HasEntityBody) return true;
            using (var ms = new MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                var s = req.InputStream;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBodyBytes) return false;
                }
                body = new UTF8Encoding(false).GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
            return true;
        }

        private void MethodNotAllowed(HttpListenerContext ctx, string allow)
        {
            try { ctx.Response.AddHeader("Allow", allow); } catch { }
            SafeRespond(ctx, 405, Error("method not allowed"));
        }

        private static string Error(string msg)
        {
            return Json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", msg } });
        }

        /// <summary>Writes a JSON response and closes it. Returns false if the client could not be reached.</summary>
        private bool SafeRespond(HttpListenerContext ctx, int status, string json)
        {
            try
            {
                var resp = ctx.Response;
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                resp.StatusCode = status;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                resp.KeepAlive = false;
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.OutputStream.Close();
                resp.Close();
                return true;
            }
            catch (Exception e)
            {
                if (running) Log("response failed: " + e.Message);
                try { ctx.Response.Abort(); } catch { }
                return false;
            }
        }

        private bool TokenMatches(string given)
        {
            string expected = Token;
            if (given == null || expected == null || given.Length != expected.Length) return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= given[i] ^ expected[i];
            return diff == 0;
        }

        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string DefaultStateDir()
        {
            string home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".colony-code");
        }

        private void WriteBridgeFile()
        {
            Directory.CreateDirectory(stateDir);
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            string json = Json.Serialize(new Dictionary<string, object>
            {
                { "port", Port }, { "token", Token }, { "pid", pid }, { "protocol", ProtocolVersion },
                { "game", gameVersion }, { "mod", modVersion },
            });
            string path = BridgeFilePath;
            string tmp = path + ".tmp";
            TryChmod(stateDir, 0x1C0); // 0700
            // Create the file empty and restrict it before the token is written into it.
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllText(tmp, "", new UTF8Encoding(false));
            TryChmod(tmp, 0x180); // 0600
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int sys_chmod(string path, int mode);

        /// <summary>chmod on macOS/Linux; a no-op on Windows or when libc cannot be reached.</summary>
        private void TryChmod(string path, int mode)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return;
            try
            {
                if (sys_chmod(path, mode) != 0)
                    Log("chmod " + Convert.ToString(mode, 8) + " failed for " + path + " (errno " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception e)
            {
                Log("chmod unavailable: " + e.GetType().Name);
            }
        }

        private void DeleteBridgeFile()
        {
            string path = BridgeFilePath;
            if (!File.Exists(path)) return;
            // Only delete the file if it is still ours (another game instance may have replaced it).
            string text = File.ReadAllText(path);
            object parsed;
            if (Json.TryParse(text, out parsed) && parsed is Dictionary<string, object> d &&
                Json.GetString(d, "token") != Token)
                return;
            File.Delete(path);
        }

        private void Log(string msg)
        {
            logs.Enqueue("[ColonyCode bridge] " + msg);
            while (logs.Count > 500 && logs.TryDequeue(out _)) { }
        }
    }
}
