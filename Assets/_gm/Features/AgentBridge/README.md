# Agent Bridge

A local command socket that lets an external tool inspect StableProjectorz and trigger
actions in it. Built for [spz-mcp](https://github.com/redDwarf03/spz-mcp), which exposes
these tools to an LLM agent over MCP, but the protocol is plain line-delimited JSON and
usable from any language.

## Enabling it

Off by default. Add `--agent-bridge` to `spz.config` (next to the executable, or at the
project root when running from the Editor), then start the app:

```
--agent-bridge
--agent-bridge-port=8765          # optional, this is the default
--agent-bridge-token=some-secret  # optional, pins a secret of your own
```

The console logs `[SPZ_Agent_Bridge] listening on 127.0.0.1:8765` on success.

**Every request must carry an access token.** If you don't pin one in `spz.config`,
the app generates a random secret on first launch and stores it at
`%LOCALAPPDATA%/StableProjectorz/agent-bridge.token` (`~/.local/share/...` elsewhere).
Clients read that same file, so in practice you configure nothing. The path is
printed in the log.

The token is not there to keep out an attacker who already runs code as you — it
removes the mode where merely enabling the bridge let *any* local process, or a
web page your browser happens to load, drive the application.

## Protocol

One JSON object per line, request and response:

```
->  {"id":"1","tool":"describe","params":{}}
<-  {"id":"1","ok":true,"result":{"protocol_version":1,"tools":[...]}}
<-  {"id":"1","ok":false,"error":"unknown tool 'foo'. Call 'describe' for the catalogue."}
```

Call `describe` first: it returns the protocol version and the full tool catalogue,
including each tool's parameters. **Clients are not meant to hard-code the tool list** —
that is what keeps this repo and the MCP server free to evolve independently.

When `--agent-bridge-token` is set, every request must include it: `{"params":{"token":"..."}}`.

## Tools

| Tool | What it does |
|---|---|
| `describe` | Protocol version + tool catalogue |
| `get_app_state` | Version, WebUI connections, loaded model, UDIM tiles, selection, generation status |
| `get_viewport_screenshot` | Base64 PNG of a viewport region |
| `list_generations` | Stored generation counts per kind, and the latest GUID |
| `list_events` | Every registered `StaticEvents` id and its parameter types |
| `invoke_event` | Fire a `StaticEvents` id, as the matching UI control would |

`list_events` + `invoke_event` are the general-purpose escape hatch: they reach the whole
UI action surface without needing a curated wrapper per button. The curated tools exist
because most event ids are internal plumbing and carry no usable description on their own.

## How it fits the codebase

* **No scene, no prefab, no Build Settings entry.** The bridge boots from
  `[RuntimeInitializeOnLoadMethod]` and lives on a `DontDestroyOnLoad` object, so this
  feature is additive files only and stays easy to rebase against upstream.
* **Threading.** The listener and each connection run on background threads; every tool
  body runs on the main thread, drained from `Update()`. Never `LateUpdate()` — that is
  where `Update_callbacks_MGR` schedules the depth, projection and render passes.
* **Async answers.** A tool may answer several frames later. `get_viewport_screenshot`
  does, because `Screenshot_MGR` completes through an async GPU readback.
* **Textures.** The screenshot callback owns its `Texture2D` and destroys it after
  encoding, per the project's rule on releasing GPU memory.

## Known limits

* `Screenshot_MGR.ScreenshotViewport_viaScript` calls `StopAllCoroutines()`, so overlapping
  captures cancel each other — including a capture the *user* starts by dragging in the
  viewport, which kills our callback before it can clear the busy latch. The latch
  therefore expires after 10 s rather than being trusted to always be cleared.
* Managers live in additively-loaded scenes, so early calls can arrive before
  `.instance` is assigned. Tools report this rather than throwing.
* Commands time out after 30 s.
* At most 8 concurrent connections, and 16 commands executed per frame; beyond that
  connections are refused and the queue simply drains over more frames.

## Security

Loopback-bound, opt-in, and token-authenticated. A connection that sends something
which isn't a valid request — bad JSON, a wrong token, an over-long line — is dropped
rather than read past, which is what keeps a cross-origin POST from a web page from
being able to fire commands at the port.

Request lines are bounded at 256 KB and read byte-by-byte through a counted reader:
`StreamReader.ReadLine()` would buffer without limit if a client never sent a newline.

None of this defends against code already running as you. It removes the wide-open
mode, nothing more. Enable the bridge only while you are actually using it.
