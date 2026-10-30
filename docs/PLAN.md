# Plan — VS Debugger Extension for Claude Code

> For setup instructions and the table of build issues already fixed, see
> [SETUP.md](SETUP.md). For how to prompt Claude Code to use this
> effectively, see [PROMPTS.md](PROMPTS.md). This file is the phased build
> plan and status history.

## Phase 1 — MCP Server (Node.js)

**Status:** first draft scaffolded in `mcp-server/`.

### Deliverables
- `package.json` — declares `@modelcontextprotocol/sdk` dependency, entrypoint, bin
- `index.js` — MCP server: tool schema + HTTP client calls to bridge
- `README.md` — install/register/test instructions

### Tool definitions
| Tool | Required params | Optional params |
|---|---|---|
| `set_breakpoint` | `file`, `line` | `condition` |
| `remove_breakpoint` | `id` OR (`file` + `line`) | |
| `list_breakpoints` | — | |
| `start_debug` | — | |
| `attach_process` | — | `processName`, `remoteServer` |
| `continue_execution` | — | |
| `step_into` / `step_over` / `step_out` | — | |
| `stop_debug` | — | |
| `get_callstack` | — | `maxFrames` |
| `get_locals` | — | `frameIndex` |
| `evaluate_expression` | `expression` | `frameIndex` |

### Test plan (Phase 1 in isolation)
1. `npm install` in `mcp-server/`
2. `node index.js` should start and log "vs-debugger MCP server ready"
3. With a mock HTTP server at `localhost:5959` returning canned JSON, verify
   the MCP server forwards calls correctly
4. Register: `claude mcp add vs-debugger -- node <path>/index.js`
5. In Claude Code: ask "list available tools" — confirm vs-debugger tools appear

---

## Phase 2 — VSIX Bridge (C#)

**Status:** first draft scaffolded in `VSDebugBridge/`. **Must be compiled
on the personal laptop with Visual Studio + extension-dev workload.**

### Deliverables
- `VSDebugBridge.csproj` — VSIX project, targets .NET Framework 4.8
- `source.extension.vsixmanifest` — manifest declaring the extension
- `DebugBridgePackage.cs` — `AsyncPackage`, starts the HTTP listener on
  VS startup; cleanly shuts it down on VS exit
- `BridgeServer.cs` — `HttpListener` on `localhost:5959`, routes endpoints
  to EnvDTE.Debugger calls, all marshaled to the UI thread

### HTTP endpoints (called by MCP server)
| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/breakpoint/set` | `{file, line, condition?}` | `{id}` |
| POST | `/breakpoint/remove` | `{id?, file?, line?}` | `{removed: bool}` |
| GET | `/breakpoints` | — | `[{id, file, line, enabled}]` |
| POST | `/debug/start` | — | `{started: bool}` |
| POST | `/debug/attach` | `{processName?, remoteServer?}` | `{attached: bool, pid}` OR `{candidates: [...]}` |
| POST | `/debug/continue` | — | `{state}` |
| POST | `/debug/step/{into\|over\|out}` | — | `{state}` |
| POST | `/debug/stop` | — | `{stopped: bool}` |
| GET | `/debug/callstack` | `?maxFrames=N` | `[{frame, file, line, function}]` |
| GET | `/debug/locals` | `?frameIndex=N` | `[{name, type, value}]` |
| POST | `/debug/evaluate` | `{expression, frameIndex?}` | `{value, type}` |

### Test plan (Phase 2 on personal laptop)
1. Open `VSDebugBridge.csproj` in Visual Studio
2. F5 (debug) — launches a VS Experimental Instance with the extension loaded
3. In the experimental instance, open any C# project
4. From a terminal: `curl http://localhost:5959/breakpoints` — should return `[]`
5. `curl -X POST http://localhost:5959/breakpoint/set -d '{"file":"Program.cs","line":10}'` — should add a breakpoint visible in the experimental VS instance
6. Once all endpoints work standalone, test end-to-end through MCP server + Claude Code

### Known risks
- EnvDTE threading errors — first wall is usually cross-thread COM exception.
  Mitigation: `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` in
  every endpoint handler before touching `DTE`.
- VSIX manifest version range — must match user's VS edition.
- `HttpListener` permission errors — using specific `localhost` hostname (not
  wildcard) avoids this, but worth testing on the actual office laptop.

---

## Phase 3 — Distribute & Run on Office Laptop

**Status:** not started. Depends on Phase 2 producing a working .vsix.

1. Build `VSDebugBridge.vsix` on personal laptop (Release config)
2. Copy `.vsix` + entire `mcp-server/` folder to office laptop
3. On office laptop:
   - Install Node.js 18+
   - Install Claude Code (CLI or Desktop)
   - `cd mcp-server && npm install`
   - `claude mcp add vs-debugger -- node <full-path>\mcp-server\index.js`
   - Double-click `VSDebugBridge.vsix` → install per-user
4. (Remote debug only) one-time credential cache:
   ```
   cmdkey /add:<msvsmon-host> /user:<DOMAIN\username> /pass
   ```

---

## Phase 4 — Integration Testing

**Status:** local debugging confirmed working end-to-end (#1-#3 below). Remote
attach (#4-#5) and Claude Code Desktop (#6) not yet tested.

### Test cases
1. **Local breakpoint:** open simple C# project, prompt "set a breakpoint at
   Program.cs line 5 and run" — confirm BP visible, execution stops — ✅ **passed**
2. **Local step:** "step over" — confirm VS step button fires, current line advances — ✅ **passed**
3. **Local inspect:** "show me locals" — confirm values match VS's Locals window — ✅ **passed**
4. **Remote attach:** "attach to w3wp.exe on staging-server" — confirm credential
   prompt appears (first time), then attach succeeds — not yet tested
5. **Remote breakpoint:** with PSGTMS hosted remotely, set BP on an actual
   bug-prone path (e.g. `BatchMaintenanceHook.cs`) — confirm hit on request — not yet tested
6. **Claude Code Desktop:** repeat #1-#3 from the Desktop app; confirm tools
   are discoverable — not yet tested

### Real-bug validation (beyond the basic test cases above)

Ran a deliberately planted, non-obvious logic bug in a standalone test console
app (`TestApplication`) — a stale-snapshot bug where a maintenance-fee check
compared each account's balance at construction time instead of its current
balance after a batch of transactions, with no comments or naming hints
pointing at the issue. In a fresh Claude Code session (no prior context on the
bug), given only the observed symptom ("account A-100 got charged a fee it
shouldn't have"), Claude:
- Set breakpoints on the transaction-apply and maintenance-fee logic via `set_breakpoint`
- Started the debug session via `start_debug`
- Hit breakpoints and used `continue`/`get_callstack` to navigate between hits
- Used `get_locals`/`evaluate_expression` to compare `referenceBalance` (the
  stale snapshot) against the live `account.Balance` at the actual breakpoint
- Correctly identified the root cause from observed runtime values — not from
  statically reading the source — and proposed the correct one-line fix

This is the strongest validation so far that the full pipeline (Claude Code →
MCP server → bridge → EnvDTE → real VS debugger) works for genuine, non-trivial
debugging — not just toy breakpoint-hit demos.

### Things that probably won't work first try
- Thread marshaling in unusual EnvDTE call paths
- Breakpoint setting before solution is loaded
- Process candidate list when remote machine has hundreds of processes
- Step commands when debugger isn't in break state (need to surface a clean error)

---

## Phase 5 — Hardening (Optional)

**Status:** not started. Only do this once Phase 4 is stable.

- Auth token on `localhost:5959` (`Authorization: Bearer <token>` header) so
  no random localhost script can drive the user's debugger
- Reconnect logic in MCP server if bridge goes down (VS restart)
- Multi-VS-instance support (currently assumes one VS at a time)
- Configurable port via env var or config file
- Telemetry/logging for debugging the bridge itself
- Push notifications for out-of-band state changes (e.g. user manually moves
  the execution pointer or steps in the VS UI) — today the bridge is pure
  request/response, so Claude only sees current state when it explicitly
  queries. See CLAUDE.md "Hard constraints" #5 for the known gap and mitigation.
