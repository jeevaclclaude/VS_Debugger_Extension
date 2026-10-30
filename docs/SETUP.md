# Setup Guide — vs-debugger MCP Server

This document covers everything needed to get the `vs-debugger` MCP server
and its Visual Studio bridge (`VSDebugBridge`) running, on either your
personal laptop (for building) or your office laptop (for daily use).

For a guide on *how to prompt Claude Code* once this is running, see
[PROMPTS.md](PROMPTS.md).

---

## 1. Prerequisites

| Requirement | Needed for |
|---|---|
| Node.js 18+ | Running the MCP server |
| Visual Studio 2022 or 2026 (any edition) | Running the bridge extension |
| "Visual Studio extension development" workload | **Only** if you're building the VSIX from source |
| Claude Code (CLI or Desktop) | Talking to the MCP server |
| **Smart App Control: OFF** (Windows 11) | Building the VSIX locally — see §5 |

To check whether Smart App Control is on:
```powershell
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy" -Name "VerifiedAndReputablePolicyState"
```
`0` = off, `1` = on. If it's on, turn it off via Settings → Privacy & security →
Windows Security → App & browser control → Smart App Control, **before**
building — it blocks locally-built, unsigned binaries from loading into VS at
all, with no per-app exception mechanism. This is a one-way switch on most
Windows builds, so confirm before proceeding.

---

## 2. Register the MCP server with Claude Code

This only needs to be done once per machine.

```bash
npm install --prefix mcp-server
claude mcp add vs-debugger -s user -- node <full-path-to-repo>\mcp-server\index.js
```

Use `-s user` (not the default project scope) — project scope keys off the
exact path-string formatting of your working directory, and a forward-slash
vs backslash mismatch will silently put the registration somewhere Claude
Code never looks. `-s user` avoids that entirely.

Verify:
```bash
claude mcp list
```
You should see `vs-debugger: ... - ✓ Connected`. **Any Claude Code session
already running before you registered it won't pick this up — start a new
session.**

---

## 3. Build the VSIX bridge

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "VSDebugBridge\VSDebugBridge.csproj" /t:Build
```

Output: `VSDebugBridge\bin\Debug\VSDebugBridge.vsix`.

If you hit build errors, they're almost certainly one of the issues already
solved during development — see §6 ("Known build issues already fixed") before
re-diagnosing from scratch.

---

## 4. Install the bridge — two different modes

### Mode A: Normal install (for actual day-to-day use)

This is what you want once the extension is stable. The bridge auto-starts
every time you open Visual Studio normally — no F5, no separate "build" VS
instance, no Experimental Instance.

1. Close **all** running `devenv.exe` instances.
2. Double-click `VSDebugBridge.vsix`.
3. Choose **"Install for: just me"** (no admin required).
4. Open Visual Studio normally and open whatever project you actually want
   to debug.
5. The bridge starts silently — confirm with:
   ```bash
   curl http://localhost:5959/breakpoints
   ```
   should return `[]`.

**Caveat:** this is a snapshot. If you rebuild the bridge source later, you
must reinstall the `.vsix` to pick up the change — a normal install does not
auto-update from source.

### Mode B: F5 debug-launch (only for developing the bridge itself)

If you're actively modifying `BridgeServer.cs`/`DebugBridgePackage.cs`, open
`VSDebugBridge.csproj` in VS and press F5. This launches a separate
**Experimental Instance** (`/rootsuffix Exp`) with your in-progress build
loaded, leaving your normal VS profile untouched. Open your target project
inside *that* instance to test against it.

Don't mix the two modes if you can avoid it — but if you do run a
normal-install VS and an Experimental Instance simultaneously, see §7.1,
"Running multiple VS instances at once" — this now works.

---

## 5. Remote debugging setup (msvsmon)

The bridge supports `attach_process` with a `remoteServer` parameter, which
uses Visual Studio's built-in remote-debugging support — no extra code is
needed on the remote side, just `msvsmon.exe` (Remote Tools for Visual
Studio), which ships **inside your VS install already**:

```
C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Remote Debugger\x64\msvsmon.exe
```

### Testing remote debugging entirely on one machine

You don't need a second physical machine to validate this path — run
`msvsmon.exe` locally and point your local VS at `localhost`:

1. Run `msvsmon.exe` (as Administrator if you want Windows Authentication
   mode, since it needs to register with the OS).
2. Accept the firewall configuration prompt — recommend leaving
   **"Informal or public networks"** unchecked, since you don't need to
   expose this to untrusted networks for a local test.
3. Note the server name/port it reports (e.g. `BOOK-1AG6GOS2V5:4026`).
4. Choose an authentication mode:
   - **Windows Authentication** — matches a real office setup. On the same
     machine/same user, this passes through with no credential prompt
     (so it validates the happy path, but not the prompt itself).
   - **No Authentication** — simplest for a pure connectivity test.

### Real remote target (separate machine)

Same steps, but `msvsmon.exe` runs on the actual remote box, and the first
attach per session may trigger a native Windows credential dialog if using
Windows Authentication — **this cannot be automated** (by design; it's a
security boundary). Either complete it manually once, or pre-cache
credentials:
```
cmdkey /add:<msvsmon-server> /user:<DOMAIN\username> /pass
```

---

## 6. Known build issues already fixed (don't re-diagnose these)

These were all hit and fixed during initial development. If you see them
again (e.g. after a fresh clone), the fix is already in the current
`.csproj`/`.cs` files — check git history / diffs before assuming it's new:

| Symptom | Root cause | Fix already applied |
|---|---|---|
| `MSB4226`/crash evaluating `.csproj` before first restore | `Microsoft.VsSDK.targets` import had no `Exists()` guard | Guarded import + restore-safety target |
| `error MSB4057: target "Build" does not exist` | `.csproj` never imported `Microsoft.CSharp.targets` | Added explicit import |
| `RuntimeIdentifiers` / NuGet restore RID error | Transitive package needed a RID-specific asset | Added `<RuntimeIdentifiers>win</RuntimeIdentifiers>` |
| `CS1069: HttpUtility not found` | Missing `System.Web` reference | Added reference |
| `Transport`/`GetProcesses` not found on `Debugger` | Those APIs live on `EnvDTE90.Debugger3`, not plain `EnvDTE.Debugger` | Cast to `Debugger3`, added `EnvDTE90`/`EnvDTE80` package refs |
| VSIX packaging error about `LICENSE.txt` | Manifest referenced a license file that doesn't exist | Removed `<License>` element |
| "Package did not load correctly" — `FileLoadException`, "outside the appbase directory" | `CodeBase` registry entries are only honored by .NET Framework for **strongly-named** assemblies | Added `[assembly: ProvideCodeBase]` + a real `.snk` strong-name key (`SignAssembly`/`AssemblyOriginatorKeyFile`) |
| Build fails with "Application Control policy has blocked this file" | Windows Smart App Control blocking the freshly-built, unsigned DLL | Turn Smart App Control off (see §1) — signing alone does **not** fix this, since Smart App Control is reputation-based, not signature-based |
| `wait_for_debugger_change` hangs/breaks the debugger on every timeout | Used `CancelAfter()` + linked `CancellationToken`, which throws `OperationCanceledException` on every ordinary timeout — VS's debugger treats that as "user-unhandled" and breaks on it, freezing the whole UI thread | Switched to `ManualResetEventSlim.Wait(int, CancellationToken)`, which returns `false` on timeout with no exception |
| `set_break_on_exceptions` times out even though it actually worked | The call genuinely takes ~25-30s (414 individual COM calls to toggle each exception type); MCP server's default 15s client timeout aborted it first | Bumped this tool's client-side timeout to 60s |
| `ExceptionGroups`/`SetBreakWhenThrown` signature errors | `Debugger.ExceptionGroups` doesn't exist on plain `EnvDTE.Debugger`; it's on `EnvDTE90a.Debugger4`. `ExceptionGroups.Item(name)` returns an `ExceptionSettings` *collection*, not a single toggleable group; `SetBreakWhenThrown(bool, ExceptionSetting)` requires a specific entry, not a group-wide flag | Cast to `Debugger4`, enumerate every entry in the named group and call `SetBreakWhenThrown` per-item |
| Two VS instances → one fails to load the extension with a generic `SetSite failed ... E_FAIL` dialog | Only one process can bind port 5959; the failure to bind wasn't caught | Wrapped `_server.Start()` in try/catch — now fails quietly with a log message instead of crashing package load (superseded by real multi-instance support — see §7.1) |

---

## 7. Running multiple VS instances at once

This used to be a hard limitation (each instance after the first got no
bridge at all). It's now properly supported:

- **Each instance picks its own port automatically.** `DebugBridgePackage`
  tries the base port (default `5959`, overridable via the
  `VSDEBUGBRIDGE_PORT` environment variable), then `+1`, `+2`, ... up to 10
  ports, until it finds one that's free. A second simultaneous instance
  typically lands on `5960`, a third on `5961`, and so on.
- **Each instance is discoverable.** Every running instance writes a small
  JSON file to `%LocalAppData%\VSDebugBridge\instances\<pid>.json`
  containing its port, loaded solution name, and start time. The file is
  deleted on clean shutdown.
- **The MCP server resolves this automatically:**
  - 0 instance files found → falls back to `localhost:5959` (old behavior,
    zero config needed for the common single-instance case).
  - Exactly 1 found → uses it automatically, no setup needed.
  - 2+ found → refuses to guess. Any tool call returns a clear error
    listing every instance (PID, port, solution name) and how to pick one.
- **Disambiguating when there are multiple:** either
  1. Tell Claude to call the `list_vs_instances` tool with `selectPid` set
     to the PID of the instance you want — this pins every subsequent tool
     call in that Claude Code session to that instance, no environment
     variable needed, or
  2. Set the `VS_BRIDGE_URL` environment variable explicitly before
     starting the MCP server (e.g. `http://localhost:5960`) — this always
     wins over auto-discovery and over `list_vs_instances` selection.

To inspect what's currently running yourself:
```powershell
Get-ChildItem "$env:LOCALAPPDATA\VSDebugBridge\instances"
```

---

## 8. Current limitations (by design, not bugs)

- **No push notifications for manual UI actions.** If you drag the execution
  pointer or edit a variable directly in VS's Locals/Watch window, Claude
  won't know until it explicitly re-queries. `wait_for_debugger_change`
  catches *execution-state* transitions (breakpoint hit, step, continue,
  mode change) but not manual value edits.
- **Windows-auth credential prompts can't be automated** — by design, this
  is a security boundary, not a gap to close.
- **`evaluate_expression` with a non-zero `frameIndex`** currently evaluates
  in the *current* frame regardless — EnvDTE doesn't cleanly expose
  "switch active frame" across all versions. Flagged for future work.
- **No "switch instance mid-task" within a single tool call** — `list_vs_instances`
  selection is sticky for the rest of the session once set, but Claude has
  to explicitly decide to call it; there's no automatic per-call instance
  routing.
- **`get_loaded_modules`'s symbol-status detection is best-effort.** The
  bridge probes several candidate property names (`SymbolStatus`,
  `HasSymbolsLoaded`, etc.) via late-bound reflection rather than a fixed
  API, since the exact property exposed varies by EnvDTE interop version.
  `symbolsLoaded` may come back `null` (unknown) even when `symbolStatusRaw`
  shows a value — read both, don't rely on `symbolsLoaded` alone.

---

## 9. Output streaming and module diagnostics (off by default)

Two newer tools are disabled until explicitly turned on, specifically so
they don't add token cost to ordinary sessions that don't need them:

- **Output/Debug window streaming** (`set_output_streaming`,
  `list_output_panes`, `get_output_snapshot`, `wait_for_output`) — captures
  text printed to VS's Output window (`Console.WriteLine`, build output,
  etc.). Turning it off clears all buffered text and stops capturing
  entirely; there's no background cost while disabled.
- **Module/symbol diagnostics** (`set_module_diagnostics`,
  `get_loaded_modules`) — lists every loaded module with `attached`
  status and best-effort symbol info, for diagnosing "stepping isn't
  working" / missing-PDB situations. Calling `get_loaded_modules` while
  disabled returns a clear error rather than silently empty data.

See [PROMPTS.md](PROMPTS.md) Scenarios 8 and 9 for example prompts.
