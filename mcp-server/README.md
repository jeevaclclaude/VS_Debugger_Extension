# vs-debugger MCP Server

The Node.js side of the VS Debugger Extension for Claude Code. Exposes
Visual Studio debugger control as MCP tools and forwards calls to the VSIX
bridge running inside Visual Studio at `localhost:5959`.

## Install

```
npm install
```

## Register with Claude Code

One-time, replacing the path with wherever you actually have this folder:

```
claude mcp add vs-debugger -- node D:\Learning\Development\Claude\vs-debugger-extension\mcp-server\index.js
```

For Claude Code Desktop specifically, if the global registration doesn't show
up in the Desktop app, drop a `.claude/mcp.json` in the project root you open:

```json
{
  "mcpServers": {
    "vs-debugger": {
      "command": "node",
      "args": ["D:\\Learning\\Development\\Claude\\vs-debugger-extension\\mcp-server\\index.js"]
    }
  }
}
```

## Configuration

`VS_BRIDGE_URL` env var overrides the default bridge target:

```
set VS_BRIDGE_URL=http://localhost:5959
node index.js
```

## Tools exposed

| Tool | Purpose |
|---|---|
| `set_breakpoint` | Set a breakpoint at file:line (with optional condition) |
| `remove_breakpoint` | Remove a breakpoint by ID or file:line |
| `list_breakpoints` | List all active breakpoints |
| `start_debug` | Start local debug session (F5) |
| `attach_process` | Attach to local or remote (msvsmon) process |
| `continue_execution` | Continue after a breakpoint |
| `step_into` / `step_over` / `step_out` | Stepping |
| `stop_debug` | Stop / detach |
| `get_callstack` | Current call stack |
| `get_locals` | Locals in current frame |
| `evaluate_expression` | Evaluate expression in current frame |

## Standalone test

Start the server:
```
node index.js
```

It will print `vs-debugger MCP server ready ...` to stderr and wait for MCP
messages on stdin. If you try a tool call and Visual Studio isn't running with
the VSDebugBridge extension loaded, you'll get a clear error pointing to that.

## Notes

- Errors from the bridge are surfaced as-is to Claude Code, including the
  remote-attach Windows-credential prompt message.
- HTTP timeout is 15s — generous because `Debugger.GetProcesses` against a
  remote msvsmon can be slow on first call.
- Communicates over stdio (per the MCP spec). All logging goes to stderr to
  avoid corrupting the stdio channel.
