using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace spz {

	// The catalogue the bridge exposes. Deliberately curated rather than auto-derived
	// from every StaticEvents id: most of those ids are internal UI plumbing
	// ("SetButtonsInteractable"), and handing an agent 87 undocumented switches is a
	// good way to get unpredictable behaviour. Broad access is still available through
	// 'list_events' + 'invoke_event' for anyone who wants it.
	//
	// Every tool must answer exactly once, via ok(...) or fail(...). Answering from a
	// later frame is fine and expected (see get_viewport_screenshot).
	public static class SPZ_Agent_Tools {

	    static readonly Dictionary<string, AgentTool> _tools = new Dictionary<string, AgentTool>(StringComparer.Ordinal);
	    static bool _registered = false;

	    public static AgentTool Find(string name){
	        if (name == null){ return null; }
	        return _tools.TryGetValue(name, out AgentTool t) ? t : null;
	    }


	    public static void RegisterAll(){
	        if (_registered){ return; }
	        _registered = true;

	        Add("describe", "Describe the tool catalogue",
	            "Protocol version and the full catalogue of available tools. Call this first.",
	            null, Tool_Describe,
	            readOnly: true, idempotent: true);

	        Add("get_app_state", "Read app state",
	            "Snapshot of the app: version, WebUI connections, loaded 3D model, UDIM tiles, selection, and whether a generation is running.",
	            null, Tool_GetAppState,
	            readOnly: true, idempotent: true);

	        // Read-only, but not idempotent: the viewport changes as the user navigates,
	        // so two identical calls legitimately return different pixels.
	        Add("get_viewport_screenshot", "Capture the viewport",
	            "PNG capture of a region of the main 3D viewport, as base64. Coordinates are viewport-normalized, (0,0) bottom-left to (1,1) top-right.",
	            new List<AgentParamDesc>{
	                new AgentParamDesc("min_x", "number", false, "Left edge, 0..1. Default 0."),
	                new AgentParamDesc("min_y", "number", false, "Bottom edge, 0..1. Default 0."),
	                new AgentParamDesc("max_x", "number", false, "Right edge, 0..1. Default 1."),
	                new AgentParamDesc("max_y", "number", false, "Top edge, 0..1. Default 1."),
	            },
	            Tool_Screenshot,
	            readOnly: true, idempotent: false, returnsImage: true);

	        Add("list_generations", "List stored generations",
	            "How many stored generations exist per kind, plus the GUID of the most recent one. Each generation keeps its camera POV, prompts, result textures and masks.",
	            null, Tool_ListGenerations,
	            readOnly: true, idempotent: true);

	        Add("list_events", "List UI event ids",
	            "Every StaticEvents id currently registered by the running UI, with the parameter types each one expects. This is the raw action surface.",
	            new List<AgentParamDesc>{
	                new AgentParamDesc("filter", "string", false, "Case-insensitive substring to narrow the list, e.g. 'Settings:'."),
	            },
	            Tool_ListEvents,
	            readOnly: true, idempotent: true);

	        // The only writing tool here. What it does depends entirely on the id, and
	        // some ids delete work (clearing generations, resetting settings), so it is
	        // flagged destructive so that a client can ask the user before firing it.
	        Add("invoke_event", "Fire a UI event",
	            "Fire a StaticEvents id, the same way the corresponding UI control would. Use list_events to discover ids and their argument types. Reports an error if the id is unknown or the arguments don't match.",
	            new List<AgentParamDesc>{
	                new AgentParamDesc("id",   "string", true,  "Event id, e.g. 'Settings:OpenSettingsPanel'."),
	                new AgentParamDesc("args", "array",  false, "Arguments, in order. Omit for a no-argument event."),
	            },
	            Tool_InvokeEvent,
	            readOnly: false, idempotent: false, destructive: true);
	    }


	    static void Add(string name, string title, string description, List<AgentParamDesc> prms,
	                    AgentToolHandler handler,
	                    bool readOnly = false, bool idempotent = false,
	                    bool destructive = false, bool returnsImage = false){
	        var desc = new AgentToolDesc{
	            name = name,
	            title = title,
	            description = description,
	            returnsImage = returnsImage,
	            readOnly = readOnly,
	            destructive = destructive,
	            idempotent = idempotent,
	            prms = prms ?? new List<AgentParamDesc>()
	        };
	        _tools[name] = new AgentTool(desc, handler);
	    }


	    // ---------------- tools ----------------

	    static void Tool_Describe(JObject prms, Action<object> ok, Action<string> fail){
	        var catalogue = new List<AgentToolDesc>();
	        foreach (var kv in _tools){ catalogue.Add(kv.Value.desc); }
	        catalogue.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

	        ok(new Dictionary<string, object>{
	            { "protocol_version", SPZ_Agent_Protocol.PROTOCOL_VERSION },
	            { "app",              SPZ_Agent_Protocol.APP_NAME },
	            { "app_version",      SP_Version.currVersion },
	            { "tools",            catalogue },
	        });
	    }


	    static void Tool_GetAppState(JObject prms, Action<object> ok, Action<string> fail){
	        var state = new Dictionary<string, object>{
	            { "app_version", SP_Version.currVersion },
	            { "sd_connected",  Connection_MGR.is_sd_connected },
	            { "gen3d_connected", Connection_MGR.is_3d_connected },
	            { "sd_url",    Connection_MGR.A1111_IP_AND_PORT },
	            { "gen3d_url", Connection_MGR.GEN3D_URL },
	        };

	        var models = ModelsHandler_3D.instance;
	        if (models == null){
	            state["model_loaded"] = false;
	            state["note"] = "ModelsHandler_3D not ready yet (scenes still loading).";
	        }else{
	            state["model_loaded"]   = models.hasModelRootGO;
	            state["model_name"]     = models.currModelRootGO_name();
	            state["is_importing"]   = models._isImportingModel;
	            state["mesh_count"]     = models.meshes?.Count ?? 0;
	            state["selected_count"] = models.selectedMeshes?.Count ?? 0;
	            state["udim_count"]     = models._allKnownUdims?.Count ?? 0;
	        }

	        var hub = StableDiffusion_Hub.instance;
	        state["is_generating"] = hub != null && hub._generating;

	        ok(state);
	    }


	    // Screenshot_MGR calls StopAllCoroutines() when it starts a capture, so a second
	    // request would silently kill the first one's callback and leave that command
	    // hanging until it times out. Serialise here instead.
	    static bool _screenshotInFlight = false;

	    static void Tool_Screenshot(JObject prms, Action<object> ok, Action<string> fail){
	        var mgr = Screenshot_MGR.instance;
	        if (mgr == null){ fail("Screenshot_MGR is not ready yet (scenes still loading)."); return; }
	        if (_screenshotInFlight){ fail("A screenshot is already in progress; retry shortly."); return; }

	        float minX = Mathf.Clamp01(ReadFloat(prms, "min_x", 0f));
	        float minY = Mathf.Clamp01(ReadFloat(prms, "min_y", 0f));
	        float maxX = Mathf.Clamp01(ReadFloat(prms, "max_x", 1f));
	        float maxY = Mathf.Clamp01(ReadFloat(prms, "max_y", 1f));
	        if (maxX <= minX || maxY <= minY){ fail("Empty region: max_x/max_y must be greater than min_x/min_y."); return; }

	        _screenshotInFlight = true;
	        try{
	            mgr.ScreenshotViewport_viaScript(new Vector2(minX, minY), new Vector2(maxX, maxY),
	                (min, max, tex) => {
	                    _screenshotInFlight = false;
	                    if (tex == null){ fail("Capture returned no texture."); return; }
	                    try{
	                        byte[] png = tex.EncodeToPNG();
	                        if (png == null){ fail("Could not encode the capture to PNG."); return; }
	                        ok(new Dictionary<string, object>{
	                            { "image_png_base64", Convert.ToBase64String(png) },
	                            { "width",  tex.width },
	                            { "height", tex.height },
	                        });
	                    }catch (Exception ex){
	                        fail($"{ex.GetType().Name} while encoding the capture: {ex.Message}");
	                    }finally{
	                        // The callback owns this texture ("plzDeleteLater").
	                        UnityEngine.Object.Destroy(tex);
	                    }
	                });
	        }catch (Exception ex){
	            _screenshotInFlight = false;
	            fail($"{ex.GetType().Name}: {ex.Message}");
	        }
	    }


	    static void Tool_ListGenerations(JObject prms, Action<object> ok, Action<string> fail){
	        var archive = GenData2D_Archive.instance;
	        if (archive == null){ fail("GenData2D_Archive is not ready yet (scenes still loading)."); return; }

	        var byKind = new Dictionary<string, int>();
	        foreach (GenerationData_Kind kind in Enum.GetValues(typeof(GenerationData_Kind))){
	            var found = archive.FindAll_GenData_ofKind(kind);
	            byKind[kind.ToString()] = found?.Count ?? 0;
	        }

	        Guid latest = archive.latestGeneration_GUID;
	        ok(new Dictionary<string, object>{
	            { "count_by_kind", byKind },
	            { "latest_guid", latest == default ? null : latest.ToString() },
	        });
	    }


	    static void Tool_ListEvents(JObject prms, Action<object> ok, Action<string> fail){
	        string filter = prms?.Value<string>("filter");
	        var listed = new List<object>();

	        foreach (string id in StaticEvents.GetRegisteredIds()){
	            if (string.IsNullOrEmpty(filter) == false &&
	                id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0){ continue; }

	            Type[] types = StaticEvents.GetParameterTypes(id);
	            var names = new List<string>();
	            if (types != null){ foreach (Type t in types){ names.Add(t.Name); } }
	            listed.Add(new Dictionary<string, object>{
	                { "id", id },
	                { "param_types", names },
	            });
	        }
	        ok(new Dictionary<string, object>{ { "events", listed }, { "count", listed.Count } });
	    }


	    static void Tool_InvokeEvent(JObject prms, Action<object> ok, Action<string> fail){
	        string id = prms?.Value<string>("id");
	        if (string.IsNullOrEmpty(id)){ fail("Missing required parameter 'id'."); return; }

	        var args = new List<object>();
	        if (prms["args"] is JArray arr){
	            foreach (JToken tok in arr){
	                if (tok.Type == JTokenType.Null){ args.Add(null); continue; }
	                // Only scalars can be mapped onto an Action<...> parameter.
	                if (tok is JValue val){ args.Add(val.Value); continue; }
	                fail($"Argument of type '{tok.Type}' is not supported; pass strings, numbers or booleans.");
	                return;
	            }
	        }

	        if (StaticEvents.TryInvokeDynamic(id, args.ToArray(), out string error) == false){
	            fail(error);
	            return;
	        }
	        ok(new Dictionary<string, object>{ { "invoked", id } });
	    }


	    // ---------------- helpers ----------------

	    static float ReadFloat(JObject prms, string key, float fallback){
	        if (prms == null){ return fallback; }
	        JToken tok = prms[key];
	        if (tok == null || tok.Type == JTokenType.Null){ return fallback; }
	        try{ return tok.Value<float>(); }catch (Exception){ return fallback; }
	    }
	}
}//end namespace
