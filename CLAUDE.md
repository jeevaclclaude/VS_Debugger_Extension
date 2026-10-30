# CLAUDE.md — Project Context for Claude Code

> **You (Claude Code) are picking up an in-progress project.** This file is your
> handoff doc. Read it fully before doing anything in this folder. The
> conversation that produced this project happened in a different Claude
> interface (claude.ai chat); this file is the distilled context.

---

## Project: VS Debugger Extension for Claude Code

### Goal (one sentence)

Let the user talk to Claude Code (CLI or Desktop) and have Claude drive Visual
Studio's actual debugger — set breakpoints, step, continue, inspect locals/call
stack/expressions — for both local AND remote (msvsmon) debugging, primarily
against the user's PSGTMS C#.NET codebase but **not language-specific**.

### Why this exists

The user's office uses Visual Studio with **msvsmon remote debugging** to debug
PSGTMS (a C#.NET banking platform for Bank of Oklahoma, under XBP Global). They
wanted Claude to debug the way a human does — interactively, with real
breakpoints in the live debugger — not just by reading files statically. No
existing Claude extension does this. So we're building one.

---

## Architecture

```
User prompt
    |
    v
Claude Code (CLI or Claude Code Desktop — same MCP registration works for both)
    |
    | MCP protocol (stdio)
    v
MCP Server  (Node.js)                          <-- mcp-server/
    |
    | HTTP localhost:5959
    v
VSIX Bridge (C#, runs inside devenv.exe)       <-- VSDebugBridge/
    |
    | EnvDTE.Debugger API (COM, must be on VS UI thread)
    v
Visual Studio's real debugger UI
    |
    | (remote only)
    v
msvsmon on remote host
    |
    v
Target process being debugged
```

### Why EnvDTE (not DAP / not Debug Adapter Protocol)

Considered three paths during planning:

1. **Reverse-engineer msvsmon's wire protocol** — closed/proprietary, not realistic.
2. **DAP via vsdbg** — works for VS Code-style remote debug, but **doesn't talk
   to msvsmon**. User's office is locked into msvsmon, so this was a dead end.
3. **EnvDTE COM automation** — drives the local VS instance's debugger from
   inside a VSIX. Because VS itself handles the msvsmon connection underneath,
   we never touch the proprietary protocol. **This is the path we picked.**

EnvDTE is generic IDE automation (predates Roslyn, predates .NET Core). It sits
*above* the language-specific debug engine, so the same code works for C#,
VB.NET, F#, C++, Python (with PTVS), or anything else VS can natively debug.

---

## Hard constraints (don't forget these)

### 1. Windows domain credentials cannot be automated

msvsmon remote attach with Windows Authentication triggers a **native Windows
NTLM/Kerberos credential dialog**. This dialog is BELOW the EnvDTE layer — no
hook to intercept it programmatically. It is also a security boundary; even if
we could automate it, we shouldn't.

**Workaround for the user (one-time per session):** either complete the dialog
manually on first attach, or pre-cache credentials via:

```
cmdkey /add:<msvsmon-server> /user:<DOMAIN\username> /pass
```

The MCP `attach_process` tool must surface a **clear error** if it hits this
wall ("Windows authentication required — complete the credential prompt in VS,
then retry") rather than failing silently.

### 2. EnvDTE is COM, must run on VS UI thread

All EnvDTE calls in the VSIX bridge MUST be marshaled to the VS UI thread.
Use `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` inside the
HTTP request handlers. Calling EnvDTE from a raw background thread will throw
cross-thread COM exceptions.

### 3. Two-machine build/run split

- **Personal laptop** (user has admin): build the VSIX here. Has Visual Studio
  Community 2026 at `C:\Program Files\Microsoft Visual Studio\18\Community`,
  with the "Visual Studio extension development" workload installed.
- **Office laptop** (no admin): install the **pre-compiled** `.vsix` per-user
  (Extensions → Manage Extensions → "Install for: just me"). Office laptop
  does NOT need the extension-dev workload. Office uses msvsmon for remote
  debugging — port typically 4022.

Worth flagging to the user before they invest more: office IT may block
per-user VSIX installs via Group Policy, may flag custom localhost HTTP
listeners via antivirus/EDR. Cheap question to ask, expensive to discover later.

### 4. Bridge starts at `localhost:5959` but auto-increments for multiple VS instances

NOT a wildcard `+`/`*` binding — Windows wildcard binds need URL ACL
reservation (admin). Specific hostname binding does not. Each VS instance tries
port 5959 first, then 5960, 5961 … up to 10 attempts. Each instance writes its
actual bound port to `%LocalAppData%\VSDebugBridge\instances\<pid>.json` so the
MCP server can discover all running instances. Use `list_vs_instances` to see
them; `list_vs_instances({ selectPid })` pins subsequent calls to one instance.

### 5. Long-poll is built — Claude can wait for state changes without polling

`wait_for_debugger_change` blocks until the next execution-state transition
(breakpoint hit, step complete, mode change, manual VS UI action) or until
`timeoutMs` elapses. Returns immediately if the debugger is already paused.

However, it only returns the **current** state at call time — if the user
drove multiple breakpoints quickly between two of Claude's calls, the
intermediate transitions are invisible to `wait_for_debugger_change`. Use
`get_debug_event_log` in that case — it returns a ring buffer of the last 200
transitions, each with a full snapshot at that exact moment.

**Still not detected:** manual edits to variable values in the Locals/Watch/
Immediate window. Those don't fire EnvDTE events. After editing a variable
directly in VS, tell Claude explicitly so it re-queries state.

### 6. Control mode — user controls what Claude is allowed to do

The bridge enforces a collaboration mode set exclusively by the user via the
VS Debug menu (Debug → Claude Debug Control). Claude can never set this itself.

| Mode | What Claude can do |
|---|---|
| `driver` | Full control — all tools allowed |
| `observer` | Read-only (callstack/locals/evaluate) — mutating calls blocked |
| `paused` | Read-only — same as observer |
| `user-driving` | Read-only — user has taken the wheel |
| `emergency-stop` | Everything blocked until user re-arms |

If any mutating tool call returns HTTP 423, call `get_control_mode` to see why,
then tell the user to switch modes via the VS UI.

### 7. Feature registry — graceful degradation under IT policy

On first access-denied exception for a specific capability, the bridge disables
that feature for the session and returns HTTP 503 for subsequent calls to it
(rather than repeatedly hitting the access wall). Call `get_feature_status` at
session start or when a tool returns 503 to see what's available. Features:
`OutputStreaming`, `ModuleDiagnostics`, `StepFilterPersistence`,
`DebugJournalPersistence`, `ClaudeLogPane`, `BreakOnExceptions`, `RemoteDebug`.

---

## Tool surface (MCP server exposes these to Claude Code)

### Core debug control

| Tool | Purpose | Notes |
|---|---|---|
| `set_breakpoint` | Set BP at file:line | Optional condition |
| `remove_breakpoint` | Remove by ID or file:line | |
| `list_breakpoints` | List active BPs | |
| `start_debug` | F5-equivalent (launch current solution) | Local only |
| `attach_process` | Attach to local or remote process | `remoteServer` param optional; returns candidate list if process name ambiguous/omitted |
| `continue_execution` | Resume execution | (named `continue_execution`, not `continue`) |
| `step_into` / `step_over` / `step_out` | Stepping | |
| `stop_debug` | Detach / stop | |

### Inspection

| Tool | Purpose | Notes |
|---|---|---|
| `get_callstack` | Current call stack | `maxFrames` param; each frame has `attached` flag |
| `get_locals` | Locals in current frame | `frameIndex` param |
| `evaluate_expression` | Evaluate in current frame | Same as VS Watch/Immediate window |

### State change notifications

| Tool | Purpose | Notes |
|---|---|---|
| `wait_for_debugger_change` | Block until next execution transition | Returns immediately if already paused; includes `suggestedStep` and up to 15 top-frame locals; pass `sinceVersion` to avoid missing events between calls |
| `get_debug_event_log` | Full history of transitions (last 200) | Use when multiple breakpoints/steps happened between calls; each entry has a full snapshot |

### Exception settings

| Tool | Purpose | Notes |
|---|---|---|
| `set_break_on_exceptions` | Pause on any CLR throw, even caught ones | Slow (20-30s) — toggles hundreds of entries one COM call at a time; disable after use |

### Output window

| Tool | Purpose | Notes |
|---|---|---|
| `set_output_streaming` | Start/stop capturing Output window text | Off by default; call first before using the two below |
| `list_output_panes` | List pane names (Debug, Build, etc.) | |
| `get_output_snapshot` | Read buffered output for a pane | `maxChars` param |
| `wait_for_output` | Block until new output appears | Long-poll; pass `sinceVersion` |

### Module/symbol diagnostics

| Tool | Purpose | Notes |
|---|---|---|
| `set_module_diagnostics` | Enable `get_loaded_modules` | Off by default |
| `get_loaded_modules` | List all loaded modules with symbol status | Only useful when stepping/BPs misbehave; requires `set_module_diagnostics(true)` first |

### Multi-instance & session management

| Tool | Purpose | Notes |
|---|---|---|
| `list_vs_instances` | List running VS instances with active bridge | Pass `selectPid` to pin subsequent calls to one instance |
| `get_control_mode` | Read current collaboration mode | Call after HTTP 423 to understand why a call was blocked |
| `get_step_filters` | List lines user marked "step over for Claude" | `wait_for_debugger_change` already includes `suggestedStep` per-line; use this for a full list |
| `get_feature_status` | See which features are enabled/disabled | Call at session start or after HTTP 503 |

### Debug journal (cross-session persistence)

| Tool | Purpose | Notes |
|---|---|---|
| `get_debug_journal` | Read persisted findings from prior sessions | **Call this first at every session start.** Contains hypothesis, focusZones, skipZones, confirmedFindings, nextSessionPlan, lastKnownState |
| `save_debug_journal` | Write Claude's analysis to the journal | Call whenever you confirm or rule out something — don't wait until session end |

---

## Repo layout (current state)

```
vs-debugger-extension/
├── CLAUDE.md                              <- you are here
├── README.md                              <- human-facing quickstart
├── docs/
│   ├── PLAN.md                            <- phased plan with status
│   ├── SETUP.md                           <- setup guide + fixed-build-issues table
│   └── PROMPTS.md                         <- how to prompt Claude Code to use this well
├── mcp-server/                            <- Phase 1
│   ├── package.json
│   ├── index.js                           <- MCP server entrypoint, all 27 tool definitions
│   └── README.md
└── VSDebugBridge/                         <- Phase 2
    ├── README.md
    ├── VSDebugBridge.csproj               <- C# project file
    ├── source.extension.vsixmanifest      <- VSIX manifest
    ├── DebugBridgePackage.cs              <- AsyncPackage entry, starts HTTP listener, writes instance discovery file
    ├── BridgeServer.cs                    <- HTTP listener, EnvDTE wrappers, all bridge logic
    ├── ClaudeControlCommands.cs           <- VS Debug menu commands (control mode, step-filter right-click)
    ├── StepOverTagger.cs                  <- Text editor tagger for step-over glyph markers
    └── StepOverGlyphFactory.cs            <- Glyph factory for the step-over margin icon
```

---

## Phase status

| Phase | What | Status |
|---|---|---|
| 1 | MCP server (Node.js) — can be fully built/tested in any sandbox | **Built, registered with Claude Code, working** |
| 2 | VSIX bridge (C#) — must be compiled on personal laptop | **Built and running on personal laptop** (see docs/PLAN.md for build fixes: VSSDK target imports, strong-name signing, Smart App Control) |
| 3 | Distribute .vsix to office laptop, install Node.js + Claude Code there, register MCP server, complete one-time credential caching | **Not started** |
| 4 | Integration testing — local breakpoints/step/inspect AND remote msvsmon attach against real PSGTMS targets | **Local breakpoints/step/inspect confirmed working, including finding a real planted logic bug via live debugging. Remote msvsmon and Claude Code Desktop not yet tested.** |
| 5 | Hardening | **Substantially complete:** long-poll (`wait_for_debugger_change`), event log ring buffer, output window streaming, module/symbol diagnostics, control mode (driver/observer/paused/user-driving/emergency-stop), step-over filters with VS glyph, multi-VS-instance support (port discovery), feature registry (graceful IT-policy degradation), debug journal (cross-session persistence). **Still not done:** auth token on bridge endpoints, reconnect logic if VS restarts. |

---

## Decisions made (and why)

- **Node.js for the MCP server** — Anthropic's official MCP SDK is most mature
  in TS/JS; matches what most MCP examples use; the user already has Node from
  other projects.
- **HTTP (not WebSocket / not named pipes) between MCP server and VSIX** —
  simplest possible: Node.js `fetch` on one side, .NET `HttpListener` on the
  other. No protocol negotiation, easy to inspect with curl during debugging.
- **Port 5959 as base, auto-increments** — each VS instance tries 5959 first,
  then 5960–5968. The MCP server discovers the actual port via instance files
  under `%LocalAppData%\VSDebugBridge\instances\`. `VS_BRIDGE_URL` env var
  overrides discovery entirely.
- **VSIX is one C# file per concern, no DI / no fancy patterns** — first draft,
  optimized for the user to read and modify, not for "enterprise" structure.
- **Control mode is user-only** — Claude can never set the collaboration mode,
  only read it. This is a security and trust boundary, not a limitation to work
  around. The VS Debug menu is the only setter path (ClaudeControlCommands.cs).
- **Debug journal lives next to the .sln file** — `.claude-debug-journal.json`
  and `.claude-step-filters.json` are written beside the solution. They should
  be gitignored (they're per-developer, not shared state).

---

## Open questions for next session

1. **Office IT policy** — confirm per-user VSIX install is allowed; confirm
   localhost listener won't be flagged by EDR. Ask before deploying.
2. **VS version at office** — the VSIX manifest targets VS 2022+. Confirm office
   VS version before building a .vsix for distribution.
3. **msvsmon authentication mode at office** — is it Windows Authentication
   (credential dialog required) or No Authentication (skipped)? Determines
   whether the cmdkey caching step is needed at all.
4. **Remote msvsmon testing** — Phase 4 remote attach against real PSGTMS targets
   not yet tested. Do this before declaring the project done.
5. **Gitignore for journal/filter files** — `.claude-debug-journal.json` and
   `.claude-step-filters.json` (written beside the .sln) should be in .gitignore
   if the PSGTMS repo uses git. Check before first use at office.

---

## User context

- **Name:** Jeeva. Based in Tamil Nadu, India. Direct communication style.
- **Day job:** Iron & steel trading (Nila Traders, Madurai) + Indian equities
  trading + IT/software side work on PSGTMS for Bank of Oklahoma via XBP Global.
- **This project's purpose:** debug PSGTMS more efficiently — the C#.NET codebase
  has recurring `.split()`-on-unvalidated-XML bugs, stored procedure issues,
  and the kind of cross-cutting problems where having Claude step through
  the live debugger beats pasting snippets back and forth.
- **Subscription:** Claude is on jeevaclclaude@gmail.com (separate from
  jeevacl@gmail.com used for Gmail/Drive automation).

---

## Working style preferences (from prior sessions)

- Direct, concise answers. No excessive caveats.
- Show files, not slabs of inline code, when the artifact will live on disk.
- Flag honest limitations (e.g. "I can't compile C# here, you'll need to test
  this part") rather than papering over them.
- When asked to plan, plan first; build only after confirmation.

---

**Next action for Claude Code in this folder:**
Read `docs/PLAN.md` for the phased breakdown, `docs/SETUP.md` before
re-diagnosing any build error (most have already been hit and fixed — check
the table there first), and `docs/PROMPTS.md` if the task involves deciding
how to phrase a debugging request. Then check `mcp-server/index.js` and
`VSDebugBridge/*.cs` to see what's already drafted. Ask the user before
making structural changes.
