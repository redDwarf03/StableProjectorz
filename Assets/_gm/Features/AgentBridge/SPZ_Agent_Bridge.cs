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
	    const int MAX_REQUEST_BYTES = 8 * 1024 * 1024;

	    int _port = DEFAULT_PORT;
	    string _token = null;                       // optional shared secret, null = no check
	    TcpListener _listener = null;
	    Thread _acceptThread = null;
	    volatile bool _isStopping = false;

	    readonly ConcurrentQueue<PendingCmd> _incoming = new ConcurrentQueue<PendingCmd>();
	    readonly List<Thread> _clientThreads = new List<Thread>();


	    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	    static void Bootstrap(){
	        if (instance != null){ return; }
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
	        _token = ReadConfigValue(TOKEN_FLAG);

	        SPZ_Agent_Tools.RegisterAll();
	        StartListening();
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
	                  + (string.IsNullOrEmpty(_token) ? "  (no token)" : "  (token required)"));
	    }


	    void AcceptLoop(){
	        while (_isStopping == false){
	            TcpClient client = null;
	            try{
	                client = _listener.AcceptTcpClient();
	            }catch (Exception){
	                break; // listener stopped, or socket torn down.
	            }
	            var t = new Thread(() => ServeClient(client)){ IsBackground = true, Name = "SPZ_AgentBridge_Client" };
	            lock (_clientThreads){ _clientThreads.Add(t); }
	            t.Start();
	        }
	    }


	    // One connection: read a request per line, hand it to the main thread, write the answer.
	    void ServeClient(TcpClient client){
	        try{
	            using (client)
	            using (NetworkStream stream = client.GetStream())
	            using (var reader = new StreamReader(stream, Encoding.UTF8))
	            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)){ AutoFlush = true, NewLine = "\n" }){

	                while (_isStopping == false){
	                    string line = reader.ReadLine();
	                    if (line == null){ break; }               // client closed
	                    if (line.Length == 0){ continue; }
	                    if (line.Length > MAX_REQUEST_BYTES){
	                        writer.WriteLine(JsonConvert.SerializeObject(AgentResponse.Fail(null, "request too large")));
	                        continue;
	                    }
	                    writer.WriteLine(HandleLine(line));
	                }
	            }
	        }catch (Exception){
	            // A dropped connection is normal; nothing to recover.
	        }
	    }


	    string HandleLine(string line){
	        AgentRequest req;
	        try{
	            req = JsonConvert.DeserializeObject<AgentRequest>(line);
	        }catch (Exception ex){
	            return JsonConvert.SerializeObject(AgentResponse.Fail(null, "malformed JSON: " + ex.Message));
	        }
	        if (req == null || string.IsNullOrEmpty(req.tool)){
	            return JsonConvert.SerializeObject(AgentResponse.Fail(req?.id, "missing 'tool'"));
	        }
	        if (string.IsNullOrEmpty(_token) == false){
	            string given = req.prms?.Value<string>("token");
	            if (string.Equals(given, _token, StringComparison.Ordinal) == false){
	                return JsonConvert.SerializeObject(AgentResponse.Fail(req.id, "invalid or missing token"));
	            }
	        }

	        var pending = new PendingCmd(req);
	        _incoming.Enqueue(pending);

	        if (pending.done.Wait(COMMAND_TIMEOUT_MS) == false){
	            pending.Abandon();
	            return JsonConvert.SerializeObject(AgentResponse.Fail(req.id, $"timed out after {COMMAND_TIMEOUT_MS} ms"));
	        }
	        return pending.responseJson;
	    }


	    // Main thread. Every tool body runs from here.
	    void Update(){
	        while (_incoming.TryDequeue(out PendingCmd cmd)){
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
