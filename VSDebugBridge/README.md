# VSDebugBridge — VSIX Bridge for Claude Code MCP

C# Visual Studio extension. Runs an HTTP server on `localhost:5959` inside
Visual Studio's process. Receives calls from the `vs-debugger` MCP server
(Node.js) and translates them into EnvDTE.Debugger API calls — driving the
real Visual Studio debugger UI.

## Build (on personal laptop)

**Prerequisites:**
- Visual Studio 2022 or 2026 with the **"Visual Studio extension development"**
  workload installed (Visual Studio Installer → Modify → Workloads tab →
  Other Toolsets)
- .NET Framework 4.8 (typically included with the workload above)

**Steps:**
1. Open `VSDebugBridge.csproj` in Visual Studio
2. Build → Build Solution (or `Ctrl+Shift+B`)
3. Output: `bin\Release\VSDebugBridge.vsix`

**To debug the extension itself:** press F5. Visual Studio launches an
*Experimental Instance* with the extension loaded — your normal VS profile is
untouched.

## Install (on office laptop or any target)

1. Copy `VSDebugBridge.vsix` to the target machine
2. Double-click it → VSIX installer opens
3. Choose **"Install for: just me"** (no admin needed)
4. Restart Visual Studio

When VS starts, the extension auto-loads and begins listening on
`http://localhost:5959/`. Check the Output window (Debug pane) for the
startup message.

## Test the bridge directly with curl

Once VS is running with the extension loaded:

```
curl http://localhost:5959/breakpoints
# []

curl -X POST http://localhost:5959/breakpoint/set ^
     -H "Content-Type: application/json" ^
     -d "{\"file\":\"C:\\path\\to\\Program.cs\",\"line\":10}"

curl http://localhost:5959/breakpoints
# [{"id":"...","file":"...","line":10,"enabled":true,...}]
```

## Endpoint reference

| Method | Path | Body | Returns |
|---|---|---|---|
| POST | `/breakpoint/set` | `{file, line, condition?}` | `{added:[ids], ...}` |
| POST | `/breakpoint/remove` | `{id?, file?, line?}` | `{removed:N}` |
| GET | `/breakpoints` | — | `[{id, file, line, enabled, condition}]` |
| POST | `/debug/start` | — | `{started, state}` |
| POST | `/debug/attach` | `{processName?, remoteServer?}` | `{attached, pid, name, ...}` OR `{candidates:[...]}` |
| POST | `/debug/continue` | — | `{state}` |
| POST | `/debug/step/{into\|over\|out}` | — | `{state}` |
| POST | `/debug/stop` | — | `{stopped:true}` |
| GET | `/debug/callstack?maxFrames=N` | — | `[{index, function, module, language}]` |
| GET | `/debug/locals?frameIndex=N` | — | `[{name, type, value}]` |
| POST | `/debug/evaluate` | `{expression, frameIndex?}` | `{value, type, isValid}` |

## Known limitations / things to verify when you build

- **Threading:** every endpoint switches to the VS UI thread via
  `JoinableTaskFactory.SwitchToMainThreadAsync()` before touching DTE. If
  you hit cross-thread COM exceptions on a specific endpoint, that's the
  first thing to check.
- **VS version:** manifest currently targets VS 17.0+ (VS 2022) through 19.0.
  Adjust `Installation` block in `source.extension.vsixmanifest` if your VS
  is older or newer.
- **Remote attach + Windows auth:** the credential dialog cannot be
  automated. First attach per session, the user completes it manually.
  Bridge returns HTTP 401 with a clear message if it detects auth failure.
- **Breakpoint IDs:** synthesized from `File:Line` since EnvDTE doesn't
  expose stable IDs. Two breakpoints on the same line would collide —
  acceptable for v0.1; revisit if it becomes a problem.
- **`evaluate_expression` with non-zero `frameIndex`:** evaluates in
  current frame currently. Switching active frame requires a different
  EnvDTE call path — flagged for Phase 5.

## Troubleshooting

- **"Cannot connect to VSIX bridge"** from MCP server: VS isn't running, or
  the extension didn't load. Check Tools → Extensions in VS.
- **Port 5959 already in use:** another instance is running. Close it,
  or change the port in `DebugBridgePackage.cs` (and `VS_BRIDGE_URL` env var
  on the MCP server side).
- **Extension installed but not loading:** check Tools → Extensions, make
  sure it's enabled. Auto-load triggers on `NoSolution` + `SolutionExists`
  contexts — should cover all cases.

## File map

| File | Purpose |
|---|---|
| `VSDebugBridge.csproj` | MSBuild project: VSIX project type, dependencies, build config |
| `source.extension.vsixmanifest` | Extension metadata, VS version targets |
| `DebugBridgePackage.cs` | `AsyncPackage` entry: starts/stops the bridge on VS startup/shutdown |
| `BridgeServer.cs` | HTTP listener, route dispatch, all EnvDTE wrappers |
