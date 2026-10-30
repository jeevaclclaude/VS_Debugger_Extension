# VS Debugger Extension for Claude Code

Drive Visual Studio's real debugger (breakpoints, step, continue, inspect locals
and call stack, evaluate expressions) from Claude Code — CLI or Desktop —
for both local AND remote (msvsmon) debugging.

## Architecture

```
You (prompt in Claude Code CLI or Desktop)
        |
        v
Claude Code ──(MCP protocol, stdio)──> MCP Server (Node.js)  [this repo: mcp-server/]
                                              |
                                              v
                              HTTP localhost:5959
                                              |
                                              v
              VSIX Bridge running inside Visual Studio  [this repo: VSDebugBridge/]
                                              |
                                              v
                                 EnvDTE.Debugger API
                                              |
                                              v
                          Visual Studio's actual debugger UI
                                              |
                                              v  (remote attach only)
                                msvsmon on remote host
```

Not C#-specific: EnvDTE is generic IDE automation. Works with any language
Visual Studio can debug natively — C#, VB.NET, F#, C++, Python (with PTVS).

## Documentation

- **[docs/SETUP.md](docs/SETUP.md)** — full setup guide: prerequisites,
  registering the MCP server, building/installing the VSIX, remote
  debugging (msvsmon) setup, and a table of every build issue already
  hit and fixed (check here before re-diagnosing a build error).
- **[docs/PROMPTS.md](docs/PROMPTS.md)** — how to prompt Claude Code to use
  this effectively: line-by-line stepping, break-on-exceptions, remote
  attach, multi-project solutions, and common anti-patterns.
- **[docs/PLAN.md](docs/PLAN.md)** — the original phased build plan and
  status history.

## Repo layout

```
vs-debugger-extension/
├── README.md            <- this file
├── docs/
│   ├── PLAN.md          <- phased build plan and status history
│   ├── SETUP.md         <- setup guide
│   └── PROMPTS.md       <- prompting guide
├── mcp-server/          <- Node.js MCP server
│   ├── package.json
│   ├── index.js
│   └── README.md
└── VSDebugBridge/       <- C# VSIX bridge
    ├── README.md
    ├── VSDebugBridge.csproj
    ├── source.extension.vsixmanifest
    ├── DebugBridgePackage.cs
    └── BridgeServer.cs
```

## Quickstart — personal laptop (build)

1. Install **Node.js 18+**.
2. Install **Visual Studio** with the **"Visual Studio extension development"** workload
   (Visual Studio Installer → Modify → Workloads tab → Other Toolsets).
3. Install **Claude Code** (CLI or Desktop) and sign in.
4. Open `mcp-server/` in a terminal:
   ```
   npm install
   ```
5. Register the MCP server with Claude Code (one-time):
   ```
   claude mcp add vs-debugger -- node D:\Learning\Development\Claude\vs-debugger-extension\mcp-server\index.js
   ```
   (Adjust path to wherever you actually extracted this.)
6. Open `VSDebugBridge/VSDebugBridge.csproj` in Visual Studio, build it (Release).
   Output `VSDebugBridge.vsix` will land in `bin\Release\`.

## Quickstart — office laptop (run)

Office laptop does NOT need Visual Studio's extension-dev workload — only:

1. Install **Node.js 18+**.
2. Install **Claude Code** (CLI or Desktop). Sign in.
3. Copy this whole folder over.
4. In `mcp-server/`: `npm install`.
5. `claude mcp add vs-debugger -- node <path>\mcp-server\index.js`.
6. Double-click the pre-built `VSDebugBridge.vsix` from the personal laptop.
   Install per-user ("Install for: just me") — no admin needed unless IT policy blocks extensions.
7. (Remote debugging only) First attach of the session: complete the Windows
   domain credential prompt manually in VS, OR pre-cache via:
   ```
   cmdkey /add:<msvsmon-server> /user:<DOMAIN\username> /pass
   ```
   This is a security boundary — credential entry is never automated.

## Usage

1. Open Visual Studio with your solution (e.g. PSGTMS).
   The VSIX bridge auto-loads and listens on `localhost:5959`.
2. Open Claude Code (CLI or Desktop).
3. Prompt naturally:
   - "Attach to PSGTMS.WebHost.exe on remote server stg-app-01 and set a breakpoint
      at BatchMaintenanceHook.cs line 45, then continue execution."
   - "Step into the next call and show me the locals."
   - "Evaluate `customer.AccountStatus` in the current frame."

Claude calls the MCP tools → bridge → EnvDTE → VS debugger UI moves in front of you.

## Status

Local debugging (breakpoints, stepping, locals, exceptions) and remote
debugging (msvsmon, tested same-machine) are both confirmed working,
including against a real planted multi-bug test case. See
[docs/PLAN.md](docs/PLAN.md) for the full phase-by-phase history, and
[docs/SETUP.md](docs/SETUP.md) for current known limitations.
