// Console tests for mod/Source/ColonyCode/Bridge (pure .NET). Run via tests/bridge/run.sh.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ColonyCode.Http;

public static class BridgeTests
{
    static int failures, passes;
    static BridgeServer server;
    static string baseUrl;

    static void Check(bool cond, string what)
    {
        if (cond) { passes++; Console.WriteLine("  ok   " + what); }
        else { failures++; Console.WriteLine("  FAIL " + what); }
    }

    struct Resp { public int Status; public string Body; public long Ms; }

    static Resp Send(string method, string path, string body = null, string token = "<server>", string origin = null, int timeoutMs = 40000, Dictionary<string, string> headers = null)
    {
        var sw = Stopwatch.StartNew();
        var req = (HttpWebRequest)WebRequest.Create(baseUrl + path);
        req.Method = method;
        req.KeepAlive = false;
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.Proxy = null;
        if (token == "<server>") token = server.Token;
        if (token != null) req.Headers["X-Colony-Token"] = token;
        if (origin != null) req.Headers["Origin"] = origin;
        if (headers != null) foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
        if (body != null)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            req.ContentType = "application/json";
            req.ContentLength = b.Length;
            using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        }
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException e) when (e.Response != null) { resp = (HttpWebResponse)e.Response; }
        using (resp)
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            var res = new Resp { Status = (int)resp.StatusCode, Body = r.ReadToEnd(), Ms = sw.ElapsedMilliseconds };
            if (path != "/health" || res.Status != 200)
                Console.WriteLine("       " + method + " " + path + " -> " + res.Status + " " + Trunc(res.Body) + " (" + res.Ms + " ms)");
            return res;
        }
    }

    static string Trunc(string s) { return s.Length > 120 ? s.Substring(0, 120) + "..." : s; }

    static Dictionary<string, object> Obj(string json) { return Json.Parse(json) as Dictionary<string, object>; }
    static List<object> Arr(string json) { return Json.Parse(json) as List<object>; }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static int Main()
    {
        ServicePointManager.DefaultConnectionLimit = 64;
        string home = Environment.GetEnvironmentVariable("HOME");
        Console.WriteLine("HOME=" + home);

        try { RunJsonTests(); RunServerTests(); RunProtocolExtrasTests(); }
        catch (Exception e) { failures++; Console.WriteLine("  FAIL unexpected exception: " + e); }
        finally { if (server != null) server.Stop(); }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASSED (" + passes + " checks)" : failures + " FAILED, " + passes + " passed");
        return failures == 0 ? 0 : 1;
    }

    static void RunJsonTests()
    {
        Console.WriteLine("json");
        var o = Obj("{\"a\":1,\"b\":[true,false,null,\"x\\n\\u00e9\\\"\"],\"c\":{\"d\":-2.5e1}, \"e\": \"\\ud83d\\ude00\"}");
        Check(o != null && (double)o["a"] == 1, "parse number");
        var b = (List<object>)o["b"];
        Check((bool)b[0] && !(bool)b[1] && b[2] == null && (string)b[3] == "x\né\"", "parse array/bool/null/escapes");
        Check(Json.GetDouble(Json.GetObject(o, "c"), "d") == -25, "nested object + exponent");
        Check(Json.GetString(o, "e") == "😀", "surrogate pair escape");
        string round = Json.Serialize(o);
        Check(Json.Serialize(Json.Parse(round)) == round, "round trip stable: " + round);
        Check(Json.Serialize(new Dictionary<string, object> { { "n", 3L }, { "f", 0.5 }, { "s", "a\u0001" } }) == "{\"n\":3,\"f\":0.5,\"s\":\"a\\u0001\"}", "serialize primitives");
        object dummy;
        Check(!Json.TryParse("{\"a\":}", out dummy) && !Json.TryParse("[1,2", out dummy) && !Json.TryParse("1 2", out dummy), "rejects malformed");
    }

    static void RunServerTests()
    {
        Console.WriteLine("server");
        // Occupy the first port so the fallback path is exercised.
        int first = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, first);
        blocker.Start();
        try
        {
            server = new BridgeServer("test-game 1.6", "0.1.0-test", first, first + 9);
            Check(server.Start(), "server starts");
        }
        finally { blocker.Stop(); }
        Check(server.Port > first && server.Port <= first + 9, "falls back to next port (" + first + " busy, got " + server.Port + ")");
        baseUrl = "http://127.0.0.1:" + server.Port;

        // bridge.json
        string expectedFile = Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".colony-code", "bridge.json");
        Check(server.BridgeFilePath == expectedFile, "bridge file under $HOME/.colony-code");
        Check(File.Exists(expectedFile), "bridge.json written");
        var bj = Obj(File.ReadAllText(expectedFile));
        Check(Json.GetLong(bj, "port") == server.Port, "bridge.json port");
        string tok = Json.GetString(bj, "token");
        Check(tok == server.Token && tok.Length == 32 && System.Text.RegularExpressions.Regex.IsMatch(tok, "^[0-9a-f]{32}$"), "bridge.json token is 32 hex");
        Check(Json.GetLong(bj, "pid") > 0 && bj.ContainsKey("protocol") && Json.GetLong(bj, "protocol", -1) == 0, "bridge.json pid/protocol");
        Check(Json.GetString(bj, "game") == "test-game 1.6" && Json.GetString(bj, "mod") == "0.1.0-test", "bridge.json game/mod");

        // auth
        Check(Send("GET", "/health", token: null).Status == 401, "health without token -> 401");
        Check(Send("GET", "/health", token: "0123456789abcdef0123456789abcdef").Status == 401, "health with wrong token -> 401");
        var h = Send("GET", "/health");
        var ho = Obj(h.Body);
        Check(h.Status == 200 && Json.GetBool(ho, "ok") && Json.GetLong(ho, "protocol", -1) == 0
              && Json.GetString(ho, "game") == "test-game 1.6" && Json.GetString(ho, "mod") == "0.1.0-test", "health with token -> 200 body");
        Check(Send("GET", "/health", origin: "http://evil.example").Status == 403, "Origin header -> 403");
        Check(Send("GET", "/health", token: null, origin: "null").Status == 403, "Origin without token -> 403");

        // events
        var ev = Send("POST", "/events", "[{\"type\":\"session.start\",\"ts\":1,\"sessionId\":\"s1\",\"cwd\":\"/x\"},{\"type\":\"agent.say\",\"ts\":2,\"sessionId\":\"s1\",\"agentId\":\"lead-s1\",\"text\":\"hi\"}]");
        Check(ev.Status == 200 && Json.GetLong(Obj(ev.Body), "accepted") == 2 && Json.GetBool(Obj(ev.Body), "ok"), "POST /events accepted 2");
        Dictionary<string, object> e1, e2, e3;
        Check(server.TryDequeueEvent(out e1) && Json.GetString(e1, "type") == "session.start" && Json.GetString(e1, "cwd") == "/x", "dequeue event 1");
        Check(server.TryDequeueEvent(out e2) && Json.GetString(e2, "type") == "agent.say" && Json.GetString(e2, "text") == "hi", "dequeue event 2");
        Check(!server.TryDequeueEvent(out e3), "event queue empty");
        Check(Send("POST", "/events", "{\"type\":\"x\"}").Status == 400, "POST /events non-array -> 400");
        Check(Send("POST", "/events", "not json").Status == 400, "POST /events bad json -> 400");

        // inbox timeout
        var t0 = Send("GET", "/inbox?wait=1");
        Check(t0.Status == 200 && Arr(t0.Body) != null && Arr(t0.Body).Count == 0 && t0.Ms >= 900, "inbox wait=1 -> [] after ~1s");
        var t00 = Send("GET", "/inbox?wait=0");
        Check(t00.Status == 200 && t00.Body == "[]" && t00.Ms < 900, "inbox wait=0 -> [] immediately");

        // inbox delivery mid-wait, and listener stays responsive during the long-poll
        Resp longPoll = default(Resp);
        var th = new Thread(() => { longPoll = Send("GET", "/inbox?wait=10"); });
        th.Start();
        Thread.Sleep(400);
        var hDuring = Send("GET", "/health");
        Check(hDuring.Status == 200 && hDuring.Ms < 2000, "health answered while inbox long-poll is open (" + hDuring.Ms + " ms)");
        string id = server.EnqueueInbox("goal.submit", new Dictionary<string, object> { { "text", "fix the tests" } });
        th.Join(15000);
        var items = Arr(longPoll.Body ?? "null");
        Check(longPoll.Status == 200 && items != null && items.Count == 1 && longPoll.Ms < 3000, "long-poll returns item enqueued mid-wait (" + longPoll.Ms + " ms)");
        var item = items != null && items.Count > 0 ? items[0] as Dictionary<string, object> : null;
        Check(Json.GetString(item, "id") == id && Json.GetString(item, "type") == "goal.submit" && Json.GetString(item, "text") == "fix the tests" && Json.GetLong(item, "ts") > 0, "inbox item has id/type/ts/payload");
        Check(item != null && !item.ContainsKey("sessionId"), "optional sessionId omitted");
        Check(Send("GET", "/inbox?wait=0").Body == "[]", "inbox item delivered only once");
        server.EnqueueInbox(new Dictionary<string, object> { { "type", "user.message" }, { "to", "all" }, { "text", "a" }, { "sessionId", "s1" } });
        server.EnqueueInbox("agent.interrupt", new Dictionary<string, object> { { "agentId", "lead-s1" } }, "s1");
        var two = Arr(Send("GET", "/inbox?wait=5").Body);
        Check(two.Count == 2 && Json.GetString((Dictionary<string, object>)two[0], "type") == "user.message"
              && Json.GetString((Dictionary<string, object>)two[1], "sessionId") == "s1", "queued items returned together, in order");

        // decisions
        var unk = Send("GET", "/decision/perm-nope?wait=0");
        Check(unk.Status == 200 && Json.GetString(Obj(unk.Body), "status") == "unknown", "unknown decision -> unknown");
        Send("POST", "/events", "[{\"type\":\"decision.open\",\"ts\":3,\"sessionId\":\"s1\",\"decision\":{\"id\":\"perm-1\",\"agentId\":\"lead-s1\",\"kind\":\"permission\",\"question\":\"Run: ls\",\"options\":[\"Allow once\",\"Deny\"]}}]");
        var open0 = Send("GET", "/decision/perm-1?wait=0");
        Check(Json.GetString(Obj(open0.Body), "status") == "open", "decision.open event registers id -> open");
        var open1 = Send("GET", "/decision/perm-1?wait=1");
        Check(Json.GetString(Obj(open1.Body), "status") == "open" && open1.Ms >= 900, "decision wait=1 -> open after ~1s");
        Resp dPoll = default(Resp);
        var dth = new Thread(() => { dPoll = Send("GET", "/decision/perm-1?wait=10"); });
        dth.Start();
        Thread.Sleep(400);
        Check(server.AnswerDecision("perm-1", "Deny", "not now"), "AnswerDecision returns true");
        dth.Join(15000);
        var dans = Obj(dPoll.Body ?? "null");
        Check(Json.GetString(dans, "status") == "answered" && Json.GetString(dans, "option") == "Deny" && Json.GetString(dans, "text") == "not now" && dPoll.Ms < 3000,
              "decision long-poll -> answered mid-wait (" + dPoll.Ms + " ms)");
        Check(!server.AnswerDecision("perm-1", "Allow once"), "second answer ignored");
        var again = Obj(Send("GET", "/decision/perm-1?wait=0").Body);
        Check(Json.GetString(again, "option") == "Deny", "answer stays readable");
        server.RegisterDecision("perm-2");
        server.AnswerDecision("perm-2", "Allow once");
        var a2 = Obj(Send("GET", "/decision/perm-2").Body);
        Check(Json.GetString(a2, "status") == "answered" && Json.GetString(a2, "option") == "Allow once" && !a2.ContainsKey("text"), "answered without text omits text");
        server.ForgetDecision("perm-2");
        Check(Json.GetString(Obj(Send("GET", "/decision/perm-2?wait=0").Body), "status") == "unknown", "ForgetDecision -> unknown");

        // 404 / 405
        Check(Send("GET", "/nope").Status == 404, "unknown path -> 404");
        Check(Send("GET", "/decision/").Status == 404, "/decision/ without id -> 404");
        Check(Send("POST", "/health", "[]").Status == 405, "POST /health -> 405");
        Check(Send("GET", "/events").Status == 405, "GET /events -> 405");
        Check(Send("POST", "/inbox", "[]").Status == 405, "POST /inbox -> 405");
        Check(Send("DELETE", "/decision/perm-1").Status == 405, "DELETE /decision/x -> 405");
        Check(Send("GET", "/nope", token: null).Status == 401, "unknown path without token -> 401 (auth first)");

        // stop releases a waiting long-poll and removes bridge.json
        Resp stopPoll = default(Resp);
        Exception stopErr = null;
        var sth = new Thread(() => { try { stopPoll = Send("GET", "/inbox?wait=20", timeoutMs: 8000); } catch (Exception ex) { stopErr = ex; } });
        sth.Start();
        Thread.Sleep(300);
        var sw = Stopwatch.StartNew();
        server.Stop();
        sth.Join(10000);
        Check(sw.ElapsedMilliseconds < 5000 && !sth.IsAlive, "Stop() returns promptly and releases long-poll (" + sw.ElapsedMilliseconds + " ms)");
        Check(!File.Exists(expectedFile), "bridge.json deleted on Stop");
        Check(!server.IsRunning && server.Port == 0, "server reports stopped");
        bool refused = false;
        try { Send("GET", "/health", token: "x", timeoutMs: 3000); } catch (WebException) { refused = true; }
        Check(refused, "connection refused after Stop");
        server.Stop(); // idempotent
    }

    static string FileMode(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("stat", "-f %Lp \"" + path + "\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return p.ExitCode == 0 ? o : null; }
        }
        catch { return null; }
    }

    // Session-filtered inbox, challenge proof, heartbeat-gated decisions, restart carry-over, file mode.
    static void RunProtocolExtrasTests()
    {
        Console.WriteLine("protocol extras");
        Check(BridgeServer.ComputeProof("Jefe", "what do ya want for nothing?") ==
              "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", "HMAC-SHA256 proof matches RFC 4231 case 2");

        int port = FreePort();
        string dir = Path.Combine(Environment.GetEnvironmentVariable("HOME"), "extras");
        server = new BridgeServer("1.6-test", "0.1.0-test", port, port, dir);
        Check(server.Start(), "extras server started");
        baseUrl = "http://127.0.0.1:" + server.Port;

        string mode = FileMode(server.BridgeFilePath);
        if (mode != null) Check(mode == "600", "bridge.json is owner-only (mode " + mode + ")");
        string dmode = FileMode(dir);
        if (dmode != null) Check(dmode == "700", "state dir is owner-only (mode " + dmode + ")");

        // challenge: tokenless /health with a challenge answers with the proof
        string nonce = "00112233445566778899aabbccddeeff";
        var ch = Send("GET", "/health", token: null, headers: new Dictionary<string, string> { { "X-Colony-Challenge", nonce } });
        Check(ch.Status == 200 && Json.GetString(Obj(ch.Body), "proof") == BridgeServer.ComputeProof(server.Token, nonce), "tokenless /health + challenge -> 200 with proof");
        Check(Send("GET", "/health", token: null, headers: new Dictionary<string, string> { { "X-Colony-Challenge", "xyz" } }).Status == 401, "bad challenge without token -> 401");
        Check(Send("GET", "/inbox?wait=0", token: null, headers: new Dictionary<string, string> { { "X-Colony-Challenge", nonce } }).Status == 401, "challenge does not open other endpoints");
        Check(Send("GET", "/health", token: "bad", headers: new Dictionary<string, string> { { "X-Colony-Challenge", nonce } }).Status == 401, "wrong token + challenge -> 401");
        Check(Send("GET", "/health", token: null, origin: "http://evil", headers: new Dictionary<string, string> { { "X-Colony-Challenge", nonce } }).Status == 403, "Origin still -> 403 with challenge");
        var withTok = Obj(Send("GET", "/health", headers: new Dictionary<string, string> { { "X-Colony-Challenge", nonce } }).Body);
        Check(Json.GetString(withTok, "proof") != null, "token + challenge -> proof included");

        // session-filtered inbox
        server.EnqueueInbox("user.message", new Dictionary<string, object> { { "to", "x" }, { "text", "for A" } }, "sessA");
        server.EnqueueInbox("user.message", new Dictionary<string, object> { { "to", "y" }, { "text", "for B" } }, "sessB");
        server.EnqueueInbox("goal.submit", new Dictionary<string, object> { { "text", "anyone" } });
        var forB = Arr(Send("GET", "/inbox?wait=0&session=sessB").Body);
        Check(forB.Count == 2 && Json.GetString((Dictionary<string, object>)forB[0], "text") == "for B" &&
              Json.GetString((Dictionary<string, object>)forB[1], "text") == "anyone", "?session=sessB gets its item + unaddressed, in order");
        Check(server.PendingInboxCount == 1, "sessA item stays queued");
        Check(Arr(Send("GET", "/inbox?wait=1&session=sessB").Body).Count == 0, "sessB poll does not take sessA item (waits, then [])");
        // a waiting sessA poller wakes for its own item only
        Resp aPoll = default(Resp);
        var ath = new Thread(() => { aPoll = Send("GET", "/inbox?wait=5&session=sessC"); });
        ath.Start();
        Thread.Sleep(200);
        server.EnqueueInbox("user.message", new Dictionary<string, object> { { "text", "for D" } }, "sessD");
        Thread.Sleep(300);
        Check(ath.IsAlive, "poller for sessC not woken by a sessD item");
        server.EnqueueInbox("user.message", new Dictionary<string, object> { { "text", "for C" } }, "sessC");
        ath.Join(6000);
        var aItems = Arr(aPoll.Body ?? "[]");
        Check(aItems.Count == 1 && Json.GetString((Dictionary<string, object>)aItems[0], "text") == "for C" && aPoll.Ms < 2000, "sessC poller gets only its item (" + aPoll.Ms + " ms)");
        var legacy = Arr(Send("GET", "/inbox?wait=0").Body);
        Check(legacy.Count == 2, "poll without ?session gets every remaining item");

        // heartbeat-gated decisions
        server.RequireHeartbeat = true;
        server.HeartbeatTimeoutSeconds = 1;
        Send("POST", "/events", "[{\"type\":\"decision.open\",\"ts\":1,\"sessionId\":\"s\",\"decision\":{\"id\":\"hb-1\",\"agentId\":\"a\",\"kind\":\"permission\",\"question\":\"q\",\"options\":[\"Allow once\",\"Deny\"]}}]");
        Check(Json.GetString(Obj(Send("GET", "/decision/hb-1?wait=0").Body), "status") == "unknown", "no heartbeat: decision.open not registered -> unknown");
        server.Heartbeat();
        Send("POST", "/events", "[{\"type\":\"decision.open\",\"ts\":1,\"sessionId\":\"s\",\"decision\":{\"id\":\"hb-2\",\"agentId\":\"a\",\"kind\":\"permission\",\"question\":\"q\",\"options\":[\"Allow once\",\"Deny\"]}}]");
        Check(Json.GetString(Obj(Send("GET", "/decision/hb-2?wait=0").Body), "status") == "open", "with heartbeat: decision.open registered -> open");
        // heartbeat stops mid-poll -> unknown well before wait expires
        var hp = Send("GET", "/decision/hb-2?wait=10");
        Check(Json.GetString(Obj(hp.Body), "status") == "unknown" && hp.Ms < 3000, "heartbeat stops mid-poll -> unknown (" + hp.Ms + " ms)");
        server.AnswerDecision("hb-2", "Deny");
        Check(Json.GetString(Obj(Send("GET", "/decision/hb-2?wait=0").Body), "status") == "answered", "answered stays readable without heartbeat");
        server.Heartbeat();
        server.RegisterDecision("hb-3");
        server.ForgetAllDecisions();
        Check(server.GetDecisionStatus("hb-3") == "unknown" && server.GetDecisionStatus("hb-2") == "unknown", "ForgetAllDecisions clears all");

        // restart carry-over
        Dictionary<string, object> drained;
        while (server.TryDequeueEvent(out drained)) { }
        server.RegisterDecision("keep-1");
        server.EnqueueInbox("goal.submit", new Dictionary<string, object> { { "text", "kept" } });
        Send("POST", "/events", "[{\"type\":\"agent.say\",\"ts\":1,\"sessionId\":\"s\",\"agentId\":\"a\",\"text\":\"kept\"}]");
        string oldToken = server.Token;
        var old = server;
        old.Stop();
        server = new BridgeServer("1.6-test", "0.1.0-test", port, port, dir);
        server.AdoptStateFrom(old);
        Check(server.Start(), "replacement server started");
        Check(server.Token == oldToken, "replacement keeps the token");
        Check(server.GetDecisionStatus("keep-1") == "open", "replacement keeps decisions");
        Check(server.PendingEventCount == 1, "replacement keeps undrained events");
        var kept = Arr(Send("GET", "/inbox?wait=0").Body);
        Check(kept.Count == 1 && Json.GetString((Dictionary<string, object>)kept[0], "text") == "kept", "replacement keeps inbox items");
        Check(File.Exists(server.BridgeFilePath) && File.ReadAllText(server.BridgeFilePath).Contains(oldToken), "bridge.json rewritten with the kept token");
        server.Stop();
    }
}
