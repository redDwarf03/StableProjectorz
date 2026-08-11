using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace spz {

	// Local command bridge, so an external agent (an MCP server, a script) can inspect
	// the app and drive it through the same actions a user would trigger from the UI.
	//
	// DESIGN NOTES
	//  * Opt-in. Disabled unless 'spz.config' contains '--agent-bridge'. An open socket
	//    that can drive the app must never be on by default.
	//  * Loopback only. The listener binds 127.0.0.1, never 0.0.0.0.
	//  * No scene, no prefab, no Build Settings entry. The bridge boots itself from
	//    [RuntimeInitializeOnLoadMethod], so this whole feature is additive files only
	//    and stays trivial to rebase against upstream.
	//  * Socket I/O happens on background threads, but every tool runs on the main
	//    thread, drained from Update(). Unity's API is main-thread only, and draining
	//    in Update() (never LateUpdate) keeps commands away from the render/projection
	//    passes that Update_callbacks_MGR schedules.
	public class SPZ_Agent_Bridge : MonoBehaviour {

	    public static SPZ_Agent_Bridge instance { get; private set; } = null;

	    public static readonly string ENABLE_FLAG = "--agent-bridge";
	    public static readonly string PORT_FLAG   = "--agent-bridge-port=";
	    public static readonly string TOKEN_FLAG  = "--agent-bridge-token=";
	    public const int DEFAULT_PORT = 8765;

	    // A command may legitimately take a while (a screenshot waits for an async GPU
	    // readback). Past this, we answer the client rather than leaving it hanging.
	    const int COMMAND_TIMEOUT_MS = 30000;

	    // Requests are small: a screenshot is a *response*. Keeping this tight bounds
	    // what a client can make us buffer before we hang up on it.
	    const int MAX_REQUEST_BYTES = 256 * 1024;

	    // A local debugging channel needs a handful of connections, not a crowd. Both
	    // caps exist so a misbehaving client degrades itself rather than the editor.
	    const int MAX_CLIENTS = 8;
	    const int MAX_CMDS_PER_FRAME = 16;

	    int _port = DEFAULT_PORT;
	    string _token = null;                       // shared secret; never null once Awake ran
	    TcpListener _listener = null;
	    Thread _acceptThread = null;
	    volatile bool _isStopping = false;
	    int _clientCount = 0;

	    readonly ConcurrentQueue<PendingCmd> _incoming = new ConcurrentQueue<PendingCmd>();


	    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	    static void Bootstrap(){
	        if (instance != null){ return; }

	        // Statics survive between play sessions when domain reload is disabled, so
	        // any latch left set by the previous session has to be cleared here.
	        SPZ_Agent_Tools.ResetTransientState();

	        if (ReadConfigFlag(ENABLE_FLAG) == false){ return; }

	        GameObject go = new GameObject(nameof(SPZ_Agent_Bridge));
	        DontDestroyOnLoad(go);
	        go.AddComponent<SPZ_Agent_Bridge>();
	    }


	    void Awake(){
	        if (instance != null){ DestroyImmediate(this.gameObject); return; }
	        instance = this;

	        string portStr = ReadConfigValue(PORT_FLAG);
	        if (string.IsNullOrEmpty(portStr) == false && int.TryParse(portStr, out int parsed)){
	            _port = parsed;
	        }

	        // A token is always required. Enabling the bridge used to mean "any process
	        // on this machine may drive the app"; generating a secret when the user
	        // hasn't chosen one removes that wide-open mode without adding a setup step,
	        // because the client reads the same file.
	        _token = ReadConfigValue(TOKEN_FLAG);
	        if (string.IsNullOrEmpty(_token)){ _token = EnsureTokenFile(); }
	        if (string.IsNullOrEmpty(_token)){
	            Debug.LogError($"<color=yellow>[{nameof(SPZ_Agent_Bridge)}]</color> no token could be established; "
	                           + $"refusing to listen. Set {TOKEN_FLAG}<secret> in {CheckForUpdates_MGR.CONFIG_FILENAME}.");
	            return;
	        }

	        SPZ_Agent_Tools.RegisterAll();
	        StartListening();
	    }


	    // Path both sides agree on without being configured. Deliberately not next to
	    // the executable: an installed copy may sit in a read-only directory.
	    public static string TokenFilePath(){
	        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
	        return Path.Combine(root, "StableProjectorz", "agent-bridge.token");
	    }


	    // Reuses an existing secret so a client that read it stays valid across restarts.
	    static string EnsureTokenFile(){
	        string path = TokenFilePath();
	        try{
	            if (File.Exists(path)){
	                string existing = File.ReadAllText(path).Trim();
	                if (existing.Length >= 16){ return existing; }
	            }
	            Directory.CreateDirectory(Path.GetDirectoryName(path));

	            byte[] raw = new byte[24];
	            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()){
	                rng.GetBytes(raw);
	            }
	            string token = Convert.ToBase64String(raw);
	            File.WriteAllText(path, token);
	            Debug.Log($"<color=yellow>[{nameof(SPZ_Agent_Bridge)}]</color> generated an access token at {path}");
	            return token;
	        }catch (Exception ex){
	            Debug.LogError($"[{nameof(SPZ_Agent_Bridge)}] could not read or create {path}: {ex.Message}");
	            return null;
	        }
	    }


	    // Length-independent comparison, so a caller cannot narrow the secret down by
	    // timing how long a rejection takes.
	    static bool TokenMatches(string given, string expected){
	        if (given == null || given.Length != expected.Length){ return false; }
	        int diff = 0;
	        for (int i = 0; i < expected.Length; i++){ diff |= given[i] ^ expected[i]; }
	        return diff == 0;
	    }


	    void StartListening(){
	        try{
	            _listener = new TcpListener(IPAddress.Loopback, _port);
	            _listener.Start();
	        }catch (Exception ex){
	            Debug.LogError($"<color=yellow>[{nameof(SPZ_Agent_Bridge)}]</color> could not listen on 127.0.0.1:{_port}  -  {ex.Message}");
	            _listener = null;
	            return;
	        }

	        _acceptThread = new Thread(AcceptLoop){ IsBackground = true, Name = "SPZ_AgentBridge_Accept" };
	        _acceptThread.Start();
	        Debug.Log($"<color=yellow>[{nameof(SPZ_Agent_Bridge)}]</color> listening on 127.0.0.1:{_port}"
	                  + $"  (token required, see {TokenFilePath()})");
	    }


	    void AcceptLoop(){
	        while (_isStopping == false){
	            TcpClient client = null;
	            try{
	                client = _listener.AcceptTcpClient();
	            }catch (Exception){
	                break; // listener stopped, or socket torn down.
	            }

	            if (Interlocked.Increment(ref _clientCount) > MAX_CLIENTS){
	                Interlocked.Decrement(ref _clientCount);
	                try{ client.Close(); }catch (Exception){ }
	                continue;
	            }
	            new Thread(() => {
	                try{ ServeClient(client); }
	                finally{ Interlocked.Decrement(ref _clientCount); }
	            }){ IsBackground = true, Name = "SPZ_AgentBridge_Client" }.Start();
	        }
	    }


	    // Bounded stand-in for StreamReader.ReadLine(), which buffers without limit when
	    // a client never sends '\n' - the size check used to run only after the whole
	    // line had already been read into memory. Returns null at end of stream.
	    static string ReadLine_bounded(Stream s, int maxBytes){
	        var buf = new List<byte>(256);
	        while (true){
	            int b = s.ReadByte();
	            if (b < 0){ return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray()); }
	            if (b == '\n'){ break; }
	            if (b == '\r'){ continue; }
	            if (buf.Count >= maxBytes){ throw new IOException($"request line exceeded {maxBytes} bytes"); }
	            buf.Add((byte)b);
	        }
	        return Encoding.UTF8.GetString(buf.ToArray());
	    }


	    // One connection: read a request per line, hand it to the main thread, write the answer.
	    void ServeClient(TcpClient client){
	        try{
	            using (client)
	            using (NetworkStream raw = client.GetStream())
	            using (var reader = new BufferedStream(raw, 8192))   // ReadByte would be a syscall each otherwise
	            using (var writer = new StreamWriter(raw, new UTF8Encoding(false)){ AutoFlush = true, NewLine = "\n" }){

	                while (_isStopping == false){
	                    string line;
	                    try{
	                        line = ReadLine_bounded(reader, MAX_REQUEST_BYTES);
	                    }catch (IOException ex){
	                        writer.WriteLine(JsonConvert.SerializeObject(AgentResponse.Fail(null, ex.Message)));
	                        break;                                   // oversized -> hang up
	                    }
	                    if (line == null){ break; }                  // client closed
	                    if (line.Length == 0){ continue; }

	                    writer.WriteLine(HandleLine(line, out bool fatal));
	                    if (fatal){ break; }
	                }
	            }
	        }catch (Exception){
	            // A dropped connection is normal; nothing to recover.
	        }
	    }


	    // 'fatal' means the caller is not speaking this protocol, so the connection is
	    // dropped instead of being read past. That also ends the one way a web page
	    // could reach us: a cross-origin POST cannot read our answer, but before this it
	    // could keep firing commands, and its HTTP preamble simply looks like bad JSON.
	    string HandleLine(string line, out bool fatal){
	        fatal = false;
	        AgentRequest req;
	        try{
	            req = JsonConvert.DeserializeObject<AgentRequest>(line);
	        }catch (Exception ex){
	            fatal = true;
	            return JsonConvert.SerializeObject(AgentResponse.Fail(null, "malformed JSON: " + ex.Message));
	        }
	        // Authenticate before validating anything else, so an unauthenticated
	        // caller cannot hold a connection slot open by sending JSON we answer.
	        string given = req?.prms?.Value<string>("token");
	        if (TokenMatches(given, _token) == false){
	            fatal = true;   // not a client of ours; don't stay on the line
	            return JsonConvert.SerializeObject(AgentResponse.Fail(req?.id, "invalid or missing token"));
	        }
	        if (req == null || string.IsNullOrEmpty(req.tool)){
	            return JsonConvert.SerializeObject(AgentResponse.Fail(req?.id, "missing 'tool'"));
	        }

	        var pending = new PendingCmd(req);
	        _incoming.Enqueue(pending);

	        if (pending.done.Wait(COMMAND_TIMEOUT_MS) == false){
	            pending.Abandon();
	            return JsonConvert.SerializeObject(AgentResponse.Fail(req.id, $"timed out after {COMMAND_TIMEOUT_MS} ms"));
	        }
	        return pending.responseJson;
	    }


	    // Main thread. Every tool body runs from here. Capped so a burst of queued
	    // commands spreads over frames instead of stalling one.
	    void Update(){
	        int handled = 0;
	        while (handled++ < MAX_CMDS_PER_FRAME && _incoming.TryDequeue(out PendingCmd cmd)){
	            if (cmd.isAbandoned){ continue; }
	            Execute(cmd);
	        }
	    }


	    void Execute(PendingCmd cmd){
	        AgentTool tool = SPZ_Agent_Tools.Find(cmd.req.tool);
	        if (tool == null){
	            cmd.Complete(AgentResponse.Fail(cmd.req.id, $"unknown tool '{cmd.req.tool}'. Call 'describe' for the catalogue."));
	            return;
	        }
	        try{
	            tool.handler(cmd.req.prms,
	                         result => cmd.Complete(AgentResponse.Ok(cmd.req.id, result)),
	                         error  => cmd.Complete(AgentResponse.Fail(cmd.req.id, error)));
	        }catch (Exception ex){
	            cmd.Complete(AgentResponse.Fail(cmd.req.id, $"{ex.GetType().Name}: {ex.Message}"));
	        }
	    }


	    void OnDestroy(){
	        if (instance != this){ return; }
	        _isStopping = true;
	        try{ _listener?.Stop(); }catch (Exception){ }
	        _listener = null;
	        instance = null;
	    }


	    // ---------- spz.config helpers ----------
	    // Same file and lookup rule as CheckForUpdates_MGR, so users have one place
	    // to configure the app.

	    static string[] ReadConfigLines(){
	        try{
	            string exeDir = Directory.GetParent(Application.dataPath).FullName;
	            string configPath = Path.Combine(exeDir, CheckForUpdates_MGR.CONFIG_FILENAME);
	            if (File.Exists(configPath) == false){ return Array.Empty<string>(); }
	            return File.ReadAllLines(configPath);
	        }catch (Exception ex){
	            Debug.LogError($"[{nameof(SPZ_Agent_Bridge)}] error reading config file: {ex.Message}");
	            return Array.Empty<string>();
	        }
	    }

	    static bool ReadConfigFlag(string flag){
	        foreach (string line in ReadConfigLines()){
	            if (line.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase)){ return true; }
	        }
	        return false;
	    }

	    static string ReadConfigValue(string flagPrefix){
	        foreach (string line in ReadConfigLines()){
	            string trimmed = line.Trim();
	            if (trimmed.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase)){
	                return trimmed.Substring(flagPrefix.Length).Trim();
	            }
	        }
	        return null;
	    }


	    // One in-flight command. The socket thread blocks on 'done' while the main
	    // thread (or an async callback several frames later) fills in the answer.
	    class PendingCmd {
	        public readonly AgentRequest req;
	        public readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
	        public string responseJson = null;

	        int _completed = 0;   // Interlocked: a tool must only answer once.
	        int _abandoned = 0;
	        public bool isAbandoned => Volatile.Read(ref _abandoned) != 0;

	        public PendingCmd(AgentRequest req){ this.req = req; }

	        public void Complete(AgentResponse resp){
	            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0){ return; }
	            try{
	                responseJson = JsonConvert.SerializeObject(resp);
	            }catch (Exception ex){
	                responseJson = JsonConvert.SerializeObject(AgentResponse.Fail(resp.id, "could not serialize result: " + ex.Message));
	            }
	            done.Set();
	        }

	        public void Abandon(){ Interlocked.Exchange(ref _abandoned, 1); }
	    }
	}
}//end namespace
