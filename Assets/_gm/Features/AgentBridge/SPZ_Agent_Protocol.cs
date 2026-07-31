using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace spz {

	// Wire format for the agent bridge. One JSON object per line ("\n"-delimited),
	// so that a client can read responses with a plain ReadLine() loop.
	//
	//   ->  {"id":"7", "tool":"get_app_state", "params":{}}
	//   <-  {"id":"7", "ok":true, "result":{...}}
	//   <-  {"id":"7", "ok":false, "error":"..."}
	//
	// The client is expected to call the 'describe' tool first: it returns this
	// protocol version plus the full catalogue of tools. Nothing is hard-coded on
	// the client side, so adding a tool here never requires releasing a new client.
	public static class SPZ_Agent_Protocol {
	    public const int PROTOCOL_VERSION = 1;
	    public const string APP_NAME = "StableProjectorz";
	}


	public class AgentRequest {
	    [JsonProperty("id")]     public string id = null;
	    [JsonProperty("tool")]   public string tool = null;
	    [JsonProperty("params")] public Newtonsoft.Json.Linq.JObject prms = null;
	}


	public class AgentResponse {
	    [JsonProperty("id")]     public string id;
	    [JsonProperty("ok")]     public bool ok;
	    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public object result;
	    [JsonProperty("error",  NullValueHandling = NullValueHandling.Ignore)] public string error;

	    public static AgentResponse Ok(string id, object result) => new AgentResponse { id = id, ok = true, result = result };
	    public static AgentResponse Fail(string id, string error) => new AgentResponse { id = id, ok = false, error = error };
	}


	// Describes one parameter of a tool, so the MCP server can build a JSON schema
	// without knowing anything about this app.
	public class AgentParamDesc {
	    [JsonProperty("name")]        public string name;
	    [JsonProperty("type")]        public string type;      // "string" | "number" | "integer" | "boolean"
	    [JsonProperty("required")]    public bool required;
	    [JsonProperty("description")] public string description;

	    public AgentParamDesc(string name, string type, bool required, string description){
	        this.name = name;  this.type = type;  this.required = required;  this.description = description;
	    }
	}


	public class AgentToolDesc {
	    [JsonProperty("name")]        public string name;
	    [JsonProperty("description")] public string description;
	    [JsonProperty("returns_image")] public bool returnsImage;   // result is {"image_png_base64": "..."}
	    [JsonProperty("params")]      public List<AgentParamDesc> prms = new List<AgentParamDesc>();
	}


	// A tool completes by calling exactly one of 'ok' or 'fail'. Both may be called
	// on a later frame than the one the tool started on (screenshots are async),
	// which is why the answer is delivered through callbacks instead of a return value.
	public delegate void AgentToolHandler(Newtonsoft.Json.Linq.JObject prms, Action<object> ok, Action<string> fail);


	public class AgentTool {
	    public readonly AgentToolDesc desc;
	    public readonly AgentToolHandler handler;

	    public AgentTool(AgentToolDesc desc, AgentToolHandler handler){
	        this.desc = desc;  this.handler = handler;
	    }
	}
}//end namespace
