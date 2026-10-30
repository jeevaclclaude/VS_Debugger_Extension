#!/usr/bin/env node
/**
 * vs-debugger MCP Server
 *
 * Exposes Visual Studio debugger control as MCP tools for Claude Code.
 * Talks to the VSIX bridge running inside Visual Studio — auto-discovers the
 * port via %LocalAppData%\VSDebugBridge\instances\*.json (falls back to
 * localhost:5959 if no discovery files exist).
 *
 * Tools: set_breakpoint, remove_breakpoint, list_breakpoints, start_debug,
 *        attach_process, continue_execution, step_into/over/out, stop_debug,
 *        get_callstack, get_locals, evaluate_expression, wait_for_debugger_change,
 *        set_break_on_exceptions, list_vs_instances, set_output_streaming,
 *        list_output_panes, get_output_snapshot, wait_for_output,
 *        set_module_diagnostics, get_loaded_modules, get_control_mode,
 *        get_debug_event_log, get_step_filters, get_feature_status,
 *        get_debug_journal, save_debug_journal,
 *        expand_variable, get_exception_info, list_threads, switch_thread,
 *        add_watch, get_watches, remove_watch,
 *        pin_variable, get_pinned_variables, unpin_variable, set_active_frame,
 *        debug_session_start (composite — always call first)
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import fs from "node:fs";
import path from "node:path";

const DEFAULT_PORT = 5959;
const BRIDGE_TIMEOUT_MS = 15000;

// ---------------------------------------------------------------------------
// Bridge discovery — multiple simultaneous VS instances each write a small
// JSON file under %LocalAppData%\VSDebugBridge\instances\<pid>.json with the
// port they actually bound (see DebugBridgePackage.WriteInstanceFile). We
// read that directory fresh on every call rather than once at startup, since
// VS instances can start/stop mid-session.
// ---------------------------------------------------------------------------

function instancesDir() {
  const base = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || "", "AppData", "Local");
  return path.join(base, "VSDebugBridge", "instances");
}

function discoverInstances() {
  const dir = instancesDir();
  let files;
  try {
    files = fs.readdirSync(dir).filter((f) => f.endsWith(".json"));
  } catch {
    return [];
  }
  const instances = [];
  for (const f of files) {
    try {
      const data = JSON.parse(fs.readFileSync(path.join(dir, f), "utf8"));
      instances.push(data);
    } catch {
      // stale/partially-written file — skip it
    }
  }
  return instances;
}

function resolveBridgeBaseUrl() {
  if (process.env.VS_BRIDGE_URL) {
    return process.env.VS_BRIDGE_URL;
  }

  // An explicit in-session choice via list_vs_instances({ selectPid }) wins
  // over auto-discovery, but not over the env var above (that's a harder
  // external override).
  if (selectedBridgeUrl) {
    return selectedBridgeUrl;
  }

  const instances = discoverInstances();
  if (instances.length === 0) {
    // No discovery files found (older bridge build, or VS not started yet) —
    // fall back to the historical default so single-instance setups keep
    // working with zero configuration.
    return `http://localhost:${DEFAULT_PORT}`;
  }
  if (instances.length === 1) {
    return `http://localhost:${instances[0].port}`;
  }

  const list = instances
    .map((i) => `  - PID ${i.pid}, port ${i.port}, solution "${i.solution}"`)
    .join("\n");
  throw new Error(
    `Multiple VS instances with an active bridge were found, so it's ambiguous which one to use:\n${list}\n` +
      `Call list_vs_instances with selectPid set to one of the PIDs above, or set the VS_BRIDGE_URL ` +
      `environment variable (e.g. "http://localhost:${instances[0].port}") to pick one explicitly.`
  );
}

// In-session override set by the list_vs_instances tool — lets Claude pick
// an instance without needing VS_BRIDGE_URL set externally. Lives only for
// the lifetime of this MCP server process (i.e. this Claude Code session).
let selectedBridgeUrl = null;

// ---------------------------------------------------------------------------
// HTTP client to VSIX bridge
// ---------------------------------------------------------------------------

async function bridgeCall(method, path, body, timeoutOverrideMs) {
  const url = `${resolveBridgeBaseUrl()}${path}`;
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(),
    timeoutOverrideMs || BRIDGE_TIMEOUT_MS
  );

  try {
    // HttpListener on the bridge side rejects POSTs with no Content-Length
    // (HTTP 411), so always send a body for POST requests.
    const requestBody = method === "GET" ? undefined : JSON.stringify(body || {});
    const res = await fetch(url, {
      method,
      headers: requestBody !== undefined ? { "Content-Type": "application/json" } : undefined,
      body: requestBody,
      signal: controller.signal,
    });

    const text = await res.text();
    let payload;
    try {
      payload = text ? JSON.parse(text) : null;
    } catch {
      payload = { raw: text };
    }

    if (!res.ok) {
      const msg =
        (payload && (payload.error || payload.message)) ||
        `Bridge returned HTTP ${res.status}`;
      throw new Error(msg);
    }

    return payload;
  } catch (err) {
    if (err.name === "AbortError") {
      throw new Error(
        `Bridge call to ${url} timed out after ${timeoutOverrideMs || BRIDGE_TIMEOUT_MS}ms. ` +
          `Is Visual Studio running with the VSDebugBridge extension loaded?`
      );
    }
    if (err.cause && err.cause.code === "ECONNREFUSED") {
      throw new Error(
        `Cannot connect to VSIX bridge at ${url}. ` +
          `Open Visual Studio with the VSDebugBridge extension installed.`
      );
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

// ---------------------------------------------------------------------------
// Composite helpers (pure Node.js — no extra bridge endpoints needed)
// ---------------------------------------------------------------------------

async function runDebugSessionStart() {
  const results = {};

  // 1. Journal — what did previous sessions find?
  try { results.journal = await bridgeCall("GET", "/debug/journal"); }
  catch (e) { results.journal = { error: e.message }; }

  // 2. Feature status — what capabilities are available on this machine?
  try { results.features = await bridgeCall("GET", "/features/status"); }
  catch (e) { results.features = { error: e.message }; }

  // 3. Current debugger state — already paused? Running? Not started?
  try {
    results.currentState = await bridgeCall("GET", "/debug/wait?timeoutMs=500", undefined, 5000);
  } catch (e) {
    results.currentState = { error: e.message };
  }

  // 4. Control mode
  try { results.controlMode = await bridgeCall("GET", "/control/mode"); }
  catch (e) { results.controlMode = { error: e.message }; }

  // 5. Active VS instances
  try {
    const instances = discoverInstances();
    results.vsInstances = { instances, count: instances.length };
  } catch (e) {
    results.vsInstances = { error: e.message };
  }

  // Build a recommended action plan from what we found
  const plan = [];
  const journal = results.journal;
  const state   = results.currentState;
  const mode    = results.controlMode?.mode;
  const features = (results.features?.features || []);
  const disabledFeatures = features.filter(f => f.state === "disabled").map(f => f.name);

  if (disabledFeatures.length > 0) {
    plan.push(`NOTICE: ${disabledFeatures.join(", ")} disabled on this machine (IT policy). Rest of debugger works normally.`);
  }

  if (mode && mode !== "driver") {
    plan.push(`NOTICE: Control mode is '${mode}'. Mutating tool calls (step/continue/breakpoints) are blocked until user sets Debug > Claude Debug Control > Let Claude Debug.`);
  }

  if (journal && !journal.error) {
    const j = journal.journal || journal;
    if (j.hypothesis)        plan.push(`JOURNAL: Prior hypothesis — ${j.hypothesis}`);
    if (j.nextSessionPlan)   plan.push(`JOURNAL: Next session plan — ${j.nextSessionPlan}`);
    if (j.focusZones?.length) plan.push(`JOURNAL: ${j.focusZones.length} focus zone(s) to step through carefully. Set breakpoints at their entry lines.`);
    if (j.skipZones?.length)  plan.push(`JOURNAL: ${j.skipZones.length} skip zone(s) confirmed safe — use continue_execution past these.`);
    if (j.lastKnownState?.file) plan.push(`JOURNAL: Last known position — ${j.lastKnownState.file}:${j.lastKnownState.line} (${j.lastKnownState.trigger})`);
    if (!j.hypothesis && !j.nextSessionPlan && !j.focusZones?.length) {
      plan.push("JOURNAL: No prior findings — debug normally and call save_debug_journal as you learn things.");
    }
  } else {
    plan.push("JOURNAL: No journal found (first session or journal not persisted). Debug normally; save_debug_journal will be written as findings accumulate.");
  }

  if (state && !state.error) {
    const s = state.state || state;
    if (s?.mode === "paused") {
      plan.push(`DEBUGGER: Already paused at ${s.file || "unknown"}:${s.line || "?"}. Inspect immediately with get_callstack + get_locals (or get_exception_info if exception is present).`);
      if (s.exception) plan.push("DEBUGGER: Exception is active — call get_exception_info for full structured details.");
    } else if (s?.mode === "running") {
      plan.push("DEBUGGER: Debug session running. Use wait_for_debugger_change to wait for the next breakpoint hit.");
    } else {
      plan.push("DEBUGGER: No active debug session. Call start_debug or attach_process to begin.");
    }
  }

  plan.push("WORKFLOW REMINDER: Pin key variables with pin_variable so they appear automatically in every wait_for_debugger_change response. Use expand_variable for complex objects instead of multiple evaluate_expression calls. Call save_debug_journal whenever you confirm or rule out something.");

  return {
    journal:      results.journal,
    features:     results.features,
    currentState: results.currentState,
    controlMode:  results.controlMode,
    vsInstances:  results.vsInstances,
    plan,
    planNote: "Follow the `plan` steps in order. This is the recommended starting point for this debug session.",
  };
}

// ---------------------------------------------------------------------------
// Tool definitions (MCP schemas)
// ---------------------------------------------------------------------------

const TOOLS = [
  {
    name: "debug_session_start",
    description:
      "ALWAYS CALL THIS FIRST at the start of every debug session — before setting breakpoints, " +
      "before calling get_callstack, before anything else. " +
      "This composite tool runs all required setup in one call: reads the debug journal (prior " +
      "session findings), checks which features are available on this machine, reads the current " +
      "debugger state, reads the control mode, and lists VS instances. " +
      "Returns a `plan` array with prioritized recommended actions for this session: " +
      "journal-based breakpoint strategy, skip zones, focus zones, any disabled features, " +
      "current debugger state, and workflow reminders. " +
      "Follow the plan items in order before doing anything else. " +
      "If the journal has a nextSessionPlan, follow it. " +
      "If focusZones exist, set breakpoints at their entry lines immediately. " +
      "If skipZones exist, plan to use continue_execution past them. " +
      "After this call, pin your key investigation variables with pin_variable so they " +
      "auto-appear in every subsequent wait_for_debugger_change response.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "set_breakpoint",
    description:
      "Set a breakpoint in Visual Studio at the given file and line. " +
      "Optionally specify a conditional expression. Returns the breakpoint ID.",
    inputSchema: {
      type: "object",
      properties: {
        file: {
          type: "string",
          description:
            "Absolute or solution-relative path to the source file (e.g. 'C:/repos/PSGTMS/BatchMaintenanceHook.cs').",
        },
        line: {
          type: "integer",
          description: "1-indexed line number.",
        },
        condition: {
          type: "string",
          description:
            "Optional conditional expression (e.g. 'customerId == 42').",
        },
      },
      required: ["file", "line"],
    },
  },
  {
    name: "remove_breakpoint",
    description:
      "Remove a breakpoint by ID, or by file+line if ID is not known.",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "Breakpoint ID from set_breakpoint." },
        file: { type: "string" },
        line: { type: "integer" },
      },
    },
  },
  {
    name: "list_breakpoints",
    description: "List all active breakpoints currently set in Visual Studio.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "start_debug",
    description:
      "Start a local debug session (F5 equivalent) for the currently open solution.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "attach_process",
    description:
      "Attach the Visual Studio debugger to a process. " +
      "If `remoteServer` is given, uses msvsmon to attach to a process on that remote machine. " +
      "If `processName` is omitted or matches multiple processes, returns a candidate list " +
      "instead of attaching — mirrors VS's manual Attach to Process dialog. " +
      "NOTE: Remote attach with Windows Authentication will trigger a native credential " +
      "prompt in VS the first time per session — that prompt must be completed manually by the user.",
    inputSchema: {
      type: "object",
      properties: {
        processName: {
          type: "string",
          description:
            "Exact process name to attach to (e.g. 'w3wp.exe', 'PSGTMS.WebHost.exe').",
        },
        remoteServer: {
          type: "string",
          description:
            "Optional remote msvsmon server, e.g. 'stg-app-01' or 'stg-app-01:4022'. Omit for local attach.",
        },
      },
    },
  },
  {
    name: "continue_execution",
    description: "Continue execution after a breakpoint hit (F5 equivalent while paused).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "step_into",
    description: "Step into the next function call (F11 equivalent).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "step_over",
    description: "Step over the next statement (F10 equivalent).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "step_out",
    description: "Step out of the current function (Shift+F11 equivalent).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "stop_debug",
    description: "Stop debugging / detach (Shift+F5 equivalent).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "get_callstack",
    description:
      "Get the current call stack frames (most recent first). Each frame now includes `parameters` " +
      "(name/type/value for each argument) so you can see what was passed to each function without " +
      "a separate evaluate_expression call. " +
      "Each frame also includes `attached: false` when that module isn't part of the currently loaded " +
      "VS solution/projects — common on large multi-project solutions where only a subset is loaded " +
      "at a time. No source or symbols are available for those frames. " +
      "WHEN TO USE: call this after every breakpoint hit to understand the call path that led here. " +
      "Then use set_active_frame to inspect a specific caller's context — useful when the bug is in " +
      "HOW a function was called, not just what it does.",
    inputSchema: {
      type: "object",
      properties: {
        maxFrames: {
          type: "integer",
          description: "Maximum number of frames to return (default 50).",
        },
      },
    },
  },
  {
    name: "get_locals",
    description:
      "Get local variables in the specified stack frame (default: current/top frame). " +
      "WHEN TO USE: call this after wait_for_debugger_change lands on a breakpoint. " +
      "For a COMPLEX OBJECT in the locals list (value shows as '{...}', a type name, or a collection " +
      "count), immediately follow up with expand_variable to drill into its members — don't use " +
      "multiple evaluate_expression calls for each property. " +
      "For a DIFFERENT FRAME's locals, call set_active_frame first, then get_locals.",
    inputSchema: {
      type: "object",
      properties: {
        frameIndex: {
          type: "integer",
          description: "Frame index (0 = top/current). Default 0.",
        },
      },
    },
  },
  {
    name: "evaluate_expression",
    description:
      "Evaluate a single expression in the current (or specified) stack frame. " +
      "Equivalent to typing in VS's Watch window or Immediate window. " +
      "PREFER expand_variable over this when you need multiple properties of the same object " +
      "— it returns the whole tree in one call instead of one call per property. " +
      "USE THIS for: computed expressions ('account.Balance * 1.1'), method calls " +
      "('list.Count'), null checks ('obj == null'), and one-off queries. " +
      "For expressions you'll check repeatedly across steps, use add_watch or pin_variable instead.",
    inputSchema: {
      type: "object",
      properties: {
        expression: {
          type: "string",
          description: "Expression to evaluate (e.g. 'customer.AccountStatus').",
        },
        frameIndex: {
          type: "integer",
          description: "Frame index for evaluation context (default 0).",
        },
      },
      required: ["expression"],
    },
  },
  {
    name: "wait_for_debugger_change",
    description:
      "Block until the debugger's execution state changes (breakpoint hit, step completed, " +
      "manual continue/F5, execution pointer moved, or debug session start/stop) — or until " +
      "timeoutMs elapses. Use this instead of polling get_callstack repeatedly when you want " +
      "to know about user-driven changes in the Visual Studio UI. " +
      "Returns IMMEDIATELY (no waiting) if the debugger is already paused or stopped at call time " +
      "— that state is itself the answer, so don't expect this to always take a while; check the " +
      "response right after calling start_debug/continue/step in case it already landed. " +
      "NOTE: this does NOT detect manual edits to variable values in the Locals/Watch/Immediate " +
      "window — only execution-state transitions. Pass the `version` from a previous response " +
      "as `sinceVersion` to avoid missing a change that happened between calls. The returned " +
      "`state.attached` field is false when execution landed in a module that isn't part of the " +
      "currently loaded VS solution/projects (no source/symbols there) — expected on large " +
      "multi-project solutions where only a subset is loaded at once. " +
      "`state.locals` includes up to 15 top-frame locals automatically when paused, so you usually " +
      "don't need a follow-up get_locals call just to see what's in scope. This is a capped " +
      "convenience, not a replacement — call get_locals directly any time you need the full " +
      "uncapped list, a different frame, or locals after this snapshot may have gone stale. " +
      "IMPORTANT: this tool only ever returns the CURRENT state at call time — if the user " +
      "drove multiple breakpoints/steps quickly between two of your calls, the intermediate " +
      "ones are invisible here. If you suspect that happened, call get_debug_event_log instead " +
      "to get the full history, not just wherever things ended up. " +
      "ALSO IMPORTANT: this tool now also wakes up when the USER changes the control mode via " +
      "the VS Debug menu (e.g. handing control back to you) — the returned `eventType` will be " +
      "'ControlModeChanged:<mode>' in that case, with no debugger execution change at all. If " +
      "you see that, call get_control_mode to confirm, and adjust your behavior accordingly — " +
      "e.g. if the user just switched you to 'driver' mode after you were waiting in observer/" +
      "paused mode, that's your cue to resume actually driving (continue/step/etc.), not just " +
      "keep passively watching. " +
      "AFTER EACH CALL — check these fields in the response and act accordingly:\n" +
      "  state.exception → not null means paused on a throw: call get_exception_info immediately.\n" +
      "  state.pins → your pinned variable values at this exact execution point, auto-included.\n" +
      "  state.locals → up to 15 top-frame locals (convenience snapshot). Complex objects show as " +
      "a type/count string — call expand_variable on any you need to drill into.\n" +
      "  state.suggestedStep → 'stepOver' means user marked this line (right-click > Mark as Step Over " +
      "for Claude) — respect it. 'stepInto' is the default.\n" +
      "  eventType → 'ControlModeChanged:driver' means user just handed control back — start driving.\n" +
      "  eventType → 'ControlModeChanged:observer/paused/user-driving' — stop mutating calls.\n" +
      "IMPORTANT: this tool only returns the CURRENT state at call time. If the user drove multiple " +
      "breakpoints quickly between calls, use get_debug_event_log to see the full history.",
    inputSchema: {
      type: "object",
      properties: {
        timeoutMs: {
          type: "integer",
          description: "Max time to wait in milliseconds (clamped 1000-120000, default 30000).",
        },
        sinceVersion: {
          type: "integer",
          description:
            "The `version` value from a previous wait_for_debugger_change/response, if any. " +
            "If the bridge's change counter has already advanced past this, returns immediately.",
        },
      },
    },
  },
  {
    name: "set_break_on_exceptions",
    description:
      "Enable or disable pausing Visual Studio's debugger the instant ANY CLR exception is " +
      "thrown, even if a try/catch elsewhere on the call stack would otherwise handle it " +
      "silently. By default VS only auto-breaks on exceptions that are truly unhandled " +
      "anywhere — realistic error-handling code (catch, log, continue) never trips that. " +
      "Enable this before running code you suspect swallows exceptions you want to inspect " +
      "live; the call stack/locals/$exception details are available via get_callstack, " +
      "get_locals, and the `exception` field returned by wait_for_debugger_change once paused. " +
      "Remember to disable it again afterward — leaving it on means VS pauses on every " +
      "exception in the process, including benign ones libraries throw internally. " +
      "NOTE: this call genuinely takes 20-30 seconds — VS toggles hundreds of individual " +
      "exception entries one COM call at a time. This is expected, not a hang or failure.",
    inputSchema: {
      type: "object",
      properties: {
        enabled: {
          type: "boolean",
          description: "true to break on every thrown exception, false to restore default behavior.",
        },
      },
    },
  },
  {
    name: "set_output_streaming",
    description:
      "Turn on/off capturing text printed to Visual Studio's Output window (e.g. " +
      "Console.WriteLine, Debug.WriteLine, build output) so it can be read back via " +
      "get_output_snapshot/wait_for_output. OFF by default — this exists so you don't pay " +
      "the token cost of streaming console noise on every debugging session, only the ones " +
      "where you actually need to see printed output instead of/alongside stepping through " +
      "locals. Turning this off clears any buffered output and stops capturing entirely " +
      "(zero ongoing cost while disabled). Calling get_output_snapshot/wait_for_output while " +
      "disabled returns a clear error rather than silently empty data.",
    inputSchema: {
      type: "object",
      properties: {
        enabled: { type: "boolean", description: "true to start capturing, false to stop and clear buffers." },
        panes: {
          type: "array",
          items: { type: "string" },
          description:
            "Optional list of pane names to capture (e.g. ['Debug']). Omit to capture all panes " +
            "(Debug, Build, and any custom panes other extensions add).",
        },
      },
    },
  },
  {
    name: "list_output_panes",
    description:
      "List the names of Visual Studio's current Output window panes (e.g. 'Debug', 'Build'). " +
      "Useful to know what pane name to pass to get_output_snapshot/wait_for_output — pane " +
      "names aren't fixed (e.g. 'Build' only exists after a build has run once).",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "get_output_snapshot",
    description:
      "Get the currently buffered text for an Output window pane (default: 'Debug'). " +
      "Requires set_output_streaming(enabled: true) to have been called first — returns an " +
      "error otherwise, since nothing was being captured.",
    inputSchema: {
      type: "object",
      properties: {
        pane: { type: "string", description: "Pane name (default 'Debug')." },
        maxChars: { type: "integer", description: "Truncate to this many most-recent characters (default 20000)." },
      },
    },
  },
  {
    name: "wait_for_output",
    description:
      "Block until new text appears in an Output window pane (default: 'Debug'), or until " +
      "timeoutMs elapses. Use this instead of polling get_output_snapshot repeatedly. Requires " +
      "set_output_streaming(enabled: true) first. Pass the `version` from a previous response " +
      "as `sinceVersion` to avoid missing output that arrived between calls.",
    inputSchema: {
      type: "object",
      properties: {
        pane: { type: "string", description: "Pane name (default 'Debug')." },
        timeoutMs: { type: "integer", description: "Max time to wait in milliseconds (clamped 1000-120000, default 30000)." },
        sinceVersion: { type: "integer", description: "The `version` from a previous call, if any." },
      },
    },
  },
  {
    name: "set_module_diagnostics",
    description:
      "Turn on/off the get_loaded_modules tool. OFF by default — this exists so you don't " +
      "reflexively call get_loaded_modules on every debugging session (it can return 50+ " +
      "entries on large solutions) when symbol/module loading isn't actually the problem. " +
      "Turn this on specifically when stepping/breakpoints aren't working as expected and you " +
      "suspect missing symbols or a module outside the loaded solution, not as a routine check.",
    inputSchema: {
      type: "object",
      properties: {
        enabled: { type: "boolean", description: "true to enable get_loaded_modules, false to disable it." },
      },
    },
  },
  {
    name: "get_loaded_modules",
    description:
      "List every module loaded in the current debug session, with `attached` (part of the " +
      "loaded VS solution or not) and best-effort symbol-load status. Use this when stepping " +
      "or breakpoints aren't behaving as expected and you suspect a missing-symbols or " +
      "outside-the-loaded-solution cause, rather than as a routine first call. Requires " +
      "set_module_diagnostics(enabled: true) first, and an active debug session " +
      "(running or paused). Symbol-status detection is best-effort: Visual Studio's exact " +
      "exposed property varies by version, so `symbolsLoaded` may be null (unknown) even " +
      "when `symbolStatusRaw` shows something — read both rather than relying on `symbolsLoaded` alone.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "get_debug_event_log",
    description:
      "Get the FULL HISTORY of debugger transitions (breakpoint hits, steps, run/break/design " +
      "mode changes) since a given version — not just the latest one. Use this instead of " +
      "wait_for_debugger_change when you suspect you missed intermediate transitions, e.g. " +
      "the user said they stepped/continued through several breakpoints quickly while you " +
      "were doing something else. wait_for_debugger_change only ever returns the CURRENT " +
      "state at the moment you call it — if multiple transitions happened between two of " +
      "your calls, the earlier ones are invisible to it. This tool returns every transition " +
      "in a bounded log (last 200 kept), each with its own mode/function/attached/exception/" +
      "locals snapshot at that exact moment, so you can reconstruct what actually happened " +
      "instead of only seeing where things ended up. Pass the highest `version` you've " +
      "already seen as `sinceVersion` to get only what's new. The log also includes " +
      "'ControlModeChanged:<mode>' entries whenever the user switches your control mode via " +
      "the VS Debug menu — useful to confirm exactly when a handoff happened if you're " +
      "reconstructing a session after the fact.",
    inputSchema: {
      type: "object",
      properties: {
        sinceVersion: {
          type: "integer",
          description: "Only return entries with version greater than this (default 0 — everything in the log).",
        },
        maxEntries: {
          type: "integer",
          description: "Cap on how many entries to return (default 50). If more exist, the most RECENT ones win, not the oldest.",
        },
      },
    },
  },
  {
    name: "get_control_mode",
    description:
      "Check the current collaboration control mode — driver (you have full control), " +
      "observer (watch only, mutating calls blocked), paused, user-driving (the user is " +
      "driving, mutating calls blocked), or emergency-stop (everything blocked, requires " +
      "manual re-arm by the user). You CANNOT set this yourself — only the user can change " +
      "it, via Visual Studio's Debug > Claude Debug Control menu. If a tool call fails with " +
      "HTTP 423, call this to see why, then explain to the user that they need to switch " +
      "modes via the VS UI before you can proceed — don't retry the blocked call.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "list_vs_instances",
    description:
      "List all currently running Visual Studio instances that have an active vs-debugger bridge " +
      "(each one is a separate devenv.exe with its own loaded solution and its own port). Use this " +
      "when a tool call fails complaining about multiple ambiguous instances, or proactively if you " +
      "need to confirm which solution you're actually talking to. " +
      "Pass `selectPid` to pin all subsequent tool calls in this session to that specific instance — " +
      "this persists until you call this tool again with a different selectPid, or the MCP server " +
      "restarts. Without selectPid, this just lists instances without changing anything.",
    inputSchema: {
      type: "object",
      properties: {
        selectPid: {
          type: "integer",
          description: "PID of the instance (from this tool's own output) to pin subsequent calls to.",
        },
      },
    },
  },
  {
    name: "get_step_filters",
    description:
      "List all lines the user has marked as 'step over for Claude' via the VS right-click context menu. " +
      "Returns an array of {file, line, fileName} entries. " +
      "You don't need to call this explicitly before stepping — wait_for_debugger_change already includes " +
      "a `suggestedStep` field ('stepInto' or 'stepOver') in its response based on the current line's " +
      "filter status. Use this tool when you want to review all marked lines for the current session, " +
      "or to double-check why a particular line triggered a step_over instead of step_into.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "get_feature_status",
    description:
      "Check which bridge features are enabled or disabled for this session. " +
      "A feature is disabled when it encountered an access/permission error on this machine " +
      "(e.g. IT policy blocking file writes, restricted COM APIs). Disabled features do NOT " +
      "affect the rest of the debugger — only that specific capability is turned off. " +
      "Call this at session start to know what's available, or when a tool returns HTTP 503 " +
      "to understand why. When a feature is disabled, tell the user clearly: what's unavailable, " +
      "what still works, and that IT policy may be the cause if they want to investigate.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "get_debug_journal",
    description:
      "Read the persisted debug journal for this solution — written across sessions so each " +
      "new debug run can benefit from what the previous one found. " +
      "Contains: hypothesis (current best theory about the bug), focusZones (file+line ranges " +
      "that need careful stepping and inspection), skipZones (areas confirmed safe to skip with " +
      "continue_execution instead of stepping), confirmedFindings (facts ruled in or out), " +
      "nextSessionPlan (specific breakpoint + expression strategy for this run), and " +
      "lastKnownState (auto-saved position/locals from the last breakpoint or session end — " +
      "written automatically by the bridge even if Claude never called save_debug_journal). " +
      "ALWAYS call this at the start of a debug session before setting any breakpoints. " +
      "If a journal exists: set breakpoints at focusZone entry lines, use continue_execution " +
      "to skip past skipZones, and evaluate the watchExpressions immediately on landing. " +
      "If no journal exists yet, debug normally and build one as you learn things.",
    inputSchema: { type: "object", properties: {} },
  },
  // ------- NEW tools -------
  {
    name: "expand_variable",
    description:
      "Drill into an object's members recursively — equivalent to clicking the + triangle " +
      "in Visual Studio's Locals or Watch window. Use this when get_locals shows a complex " +
      "object (Dictionary, List, custom class) and you need to inspect its fields/properties " +
      "without manually writing evaluate_expression for each one. " +
      "Returns the root value plus a tree of children up to maxDepth levels deep. " +
      "Each node includes name, type, value, hasChildren, and children (if expanded). " +
      "Requires the debugger to be paused.",
    inputSchema: {
      type: "object",
      properties: {
        expression: {
          type: "string",
          description: "Expression to expand (e.g. 'account', 'processor._customerProfiles', 'ex.InnerException').",
        },
        maxDepth: {
          type: "integer",
          description: "How many levels deep to expand (default 2, max 5). Higher values are slower for large objects.",
        },
        frameIndex: {
          type: "integer",
          description: "Stack frame index for evaluation context (default 0 = top frame).",
        },
      },
      required: ["expression"],
    },
  },
  {
    name: "get_exception_info",
    description:
      "Get structured details about the current exception — type, message, source, stack trace, " +
      "and inner exceptions — when the debugger is paused on a throw. Equivalent to inspecting " +
      "$exception in the Watch/Immediate window but returns structured fields so you don't have " +
      "to evaluate each property manually. Returns { hasException: false } if not paused on an exception.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "list_threads",
    description:
      "List all threads in the currently debugged process. Each thread shows its ID, name, " +
      "whether it is the current thread, and suspend count. Use this when debugging multi-threaded " +
      "code (ASP.NET request threads, background workers) to see what is running and identify the " +
      "thread you want to inspect. Requires an active debug session.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "switch_thread",
    description:
      "Switch the active thread so that get_callstack, get_locals, and evaluate_expression " +
      "operate in the context of a different thread. Pass the thread ID from list_threads. " +
      "Only available while the debugger is paused.",
    inputSchema: {
      type: "object",
      properties: {
        threadId: {
          type: "integer",
          description: "Thread ID from list_threads.",
        },
      },
      required: ["threadId"],
    },
  },
  {
    name: "add_watch",
    description:
      "Add a named watch expression. Watch expressions are evaluated on demand when you call " +
      "get_watches — useful for tracking a set of expressions you want to check periodically " +
      "without re-typing them. Unlike pin_variable, watches are NOT auto-included in every " +
      "wait_for_debugger_change response. Use watches for context you check occasionally; " +
      "use pin_variable for values you need after every single step.",
    inputSchema: {
      type: "object",
      properties: {
        name:       { type: "string", description: "A label for this watch (e.g. 'balance', 'ownerRegion')." },
        expression: { type: "string", description: "Expression to evaluate (e.g. 'account.Balance', 'processor._savingsAccounts.Count')." },
      },
      required: ["name", "expression"],
    },
  },
  {
    name: "get_watches",
    description:
      "Evaluate all named watch expressions added via add_watch and return their current values. " +
      "Requires the debugger to be paused. If not paused, returns the list without values.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "remove_watch",
    description: "Remove a named watch expression added via add_watch.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "The watch name to remove." },
      },
      required: ["name"],
    },
  },
  {
    name: "pin_variable",
    description:
      "Pin a named expression so it is auto-evaluated and included in EVERY " +
      "wait_for_debugger_change response under the `pins` field — no follow-up call needed. " +
      "This is the most efficient way to track a value across many steps: pin it once and " +
      "it appears in the snapshot after every breakpoint hit or step, alongside locals. " +
      "Use pins for variables that are critical to your current investigation (e.g. the " +
      "transaction amount, the customer ID, a flag you are tracing). Use add_watch for " +
      "expressions you only check occasionally.",
    inputSchema: {
      type: "object",
      properties: {
        name:       { type: "string", description: "A label for this pin (e.g. 'balance', 'customerId')." },
        expression: { type: "string", description: "Expression to evaluate automatically (e.g. 'account.Balance', 'owner.Region')." },
      },
      required: ["name", "expression"],
    },
  },
  {
    name: "get_pinned_variables",
    description:
      "List all pinned variables and their current values. " +
      "You normally don't need to call this explicitly since pinned values appear automatically " +
      "in wait_for_debugger_change responses under `state.pins`. Use this to review what is " +
      "pinned or to get fresh values without waiting for the next state change.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unpin_variable",
    description: "Remove a pinned variable so it stops being auto-evaluated in snapshots.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "The pin name to remove." },
      },
      required: ["name"],
    },
  },
  {
    name: "set_active_frame",
    description:
      "Change which stack frame get_locals and evaluate_expression operate in — same as " +
      "clicking a frame in Visual Studio's Call Stack window. After calling this, get_locals " +
      "returns that frame's locals and evaluate_expression evaluates in that frame's context. " +
      "The effect persists until the next step, continue, or thread switch. " +
      "Use get_callstack first to see frame indexes. Only available while paused.",
    inputSchema: {
      type: "object",
      properties: {
        frameIndex: {
          type: "integer",
          description: "Frame index from get_callstack (0 = top/current frame).",
        },
      },
      required: ["frameIndex"],
    },
  },
  {
    name: "get_vs_selection",
    description:
      "Read the code the user most recently sent from Visual Studio via right-click → " +
      "'Send Selection to Claude Code'. Returns the selected text, file path, and line range. " +
      "Use this when the user says 'look at this', 'explain this', 'what's wrong here', or " +
      "pastes no code but clearly wants you to inspect something they highlighted in VS. " +
      "If nothing has been sent yet, returns available:false with instructions.",
    inputSchema: { type: "object", properties: {} },
  },
  // ------- END new tools -------
  {
    name: "save_debug_journal",
    description:
      "Write your analysis and findings into the debug journal so the next session starts " +
      "smarter. Call this whenever you confirm or rule out something meaningful — don't wait " +
      "until session end, since VS or Claude may stop unexpectedly. The bridge auto-saves " +
      "lastKnownState (position + locals) on every breakpoint and session end, so that part " +
      "is always safe. This tool is for Claude's interpretation: hypothesis, focusZones, " +
      "skipZones, confirmedFindings, nextSessionPlan. " +
      "Good times to call this: after confirming a zone is safe to skip, after finding a " +
      "suspicious variable value, after ruling out a hypothesis, or when switching from " +
      "observer to driver mode and you have a new theory.",
    inputSchema: {
      type: "object",
      properties: {
        hypothesis: {
          type: "string",
          description: "Current best theory about the root cause of the bug.",
        },
        focusZones: {
          type: "array",
          description: "Line ranges needing careful step+inspect next session.",
          items: {
            type: "object",
            properties: {
              file:            { type: "string" },
              lineStart:       { type: "integer" },
              lineEnd:         { type: "integer" },
              reason:          { type: "string" },
              watchExpressions: { type: "array", items: { type: "string" } },
            },
          },
        },
        skipZones: {
          type: "array",
          description: "Line ranges confirmed safe — use continue_execution past these next session.",
          items: {
            type: "object",
            properties: {
              file:      { type: "string" },
              lineStart: { type: "integer" },
              lineEnd:   { type: "integer" },
              reason:    { type: "string" },
            },
          },
        },
        confirmedFindings: {
          type: "array",
          description: "Facts confirmed true or ruled out this session.",
          items: { type: "string" },
        },
        nextSessionPlan: {
          type: "string",
          description: "Concrete plan for the next debug run: which breakpoint to set first, which expressions to evaluate immediately.",
        },
      },
    },
  },
];

// ---------------------------------------------------------------------------
// Tool dispatch — maps each tool call to a bridge HTTP endpoint
// ---------------------------------------------------------------------------

async function dispatchTool(name, args) {
  args = args || {};
  switch (name) {
    case "debug_session_start":
      return runDebugSessionStart();

    case "set_breakpoint":
      return bridgeCall("POST", "/breakpoint/set", {
        file: args.file,
        line: args.line,
        condition: args.condition,
      });

    case "remove_breakpoint":
      return bridgeCall("POST", "/breakpoint/remove", {
        id: args.id,
        file: args.file,
        line: args.line,
      });

    case "list_breakpoints":
      return bridgeCall("GET", "/breakpoints");

    case "start_debug":
      return bridgeCall("POST", "/debug/start");

    case "attach_process":
      return bridgeCall("POST", "/debug/attach", {
        processName: args.processName,
        remoteServer: args.remoteServer,
      });

    case "continue_execution":
      return bridgeCall("POST", "/debug/continue");

    case "step_into":
      return bridgeCall("POST", "/debug/step/into");

    case "step_over":
      return bridgeCall("POST", "/debug/step/over");

    case "step_out":
      return bridgeCall("POST", "/debug/step/out");

    case "stop_debug":
      return bridgeCall("POST", "/debug/stop");

    case "get_callstack": {
      const max = args.maxFrames ? `?maxFrames=${args.maxFrames}` : "";
      return bridgeCall("GET", `/debug/callstack${max}`);
    }

    case "get_locals": {
      const fi = args.frameIndex != null ? `?frameIndex=${args.frameIndex}` : "";
      return bridgeCall("GET", `/debug/locals${fi}`);
    }

    case "evaluate_expression":
      return bridgeCall("POST", "/debug/evaluate", {
        expression: args.expression,
        frameIndex: args.frameIndex,
      });

    case "wait_for_debugger_change": {
      const timeoutMs = args.timeoutMs || 30000;
      const params = new URLSearchParams({ timeoutMs: String(timeoutMs) });
      if (args.sinceVersion != null) params.set("sinceVersion", String(args.sinceVersion));
      // Give the HTTP client a bit more headroom than the bridge's own
      // timeout so a slow-but-legitimate response isn't aborted client-side.
      return bridgeCall("GET", `/debug/wait?${params}`, undefined, timeoutMs + 10000);
    }

    case "set_break_on_exceptions":
      // Enumerates and toggles every entry under "Common Language Runtime
      // Exceptions" one COM call at a time (hundreds of entries) — this
      // genuinely takes 20-30s, well past the default bridge timeout.
      return bridgeCall(
        "POST",
        "/debug/break-on-exceptions",
        { enabled: args.enabled !== false },
        60000
      );

    case "set_output_streaming":
      return bridgeCall("POST", "/output/streaming", {
        enabled: args.enabled !== false,
        panes: args.panes,
      });

    case "list_output_panes":
      return bridgeCall("GET", "/output/panes");

    case "get_output_snapshot": {
      const params = new URLSearchParams();
      if (args.pane) params.set("pane", args.pane);
      if (args.maxChars) params.set("maxChars", String(args.maxChars));
      const qs = params.toString();
      return bridgeCall("GET", `/output/snapshot${qs ? `?${qs}` : ""}`);
    }

    case "wait_for_output": {
      const timeoutMs = args.timeoutMs || 30000;
      const params = new URLSearchParams({ timeoutMs: String(timeoutMs) });
      if (args.pane) params.set("pane", args.pane);
      if (args.sinceVersion != null) params.set("sinceVersion", String(args.sinceVersion));
      return bridgeCall("GET", `/output/tail?${params}`, undefined, timeoutMs + 10000);
    }

    case "set_module_diagnostics":
      return bridgeCall("POST", "/debug/module-diagnostics", { enabled: args.enabled !== false });

    case "get_loaded_modules":
      return bridgeCall("GET", "/debug/modules");

    case "get_debug_event_log": {
      const params = new URLSearchParams();
      if (args.sinceVersion != null) params.set("sinceVersion", String(args.sinceVersion));
      if (args.maxEntries != null) params.set("maxEntries", String(args.maxEntries));
      const qs = params.toString();
      return bridgeCall("GET", `/debug/eventlog${qs ? `?${qs}` : ""}`);
    }

    case "get_control_mode":
      return bridgeCall("GET", "/control/mode");

    case "get_step_filters":
      return bridgeCall("GET", "/debug/stepfilters");

    case "get_feature_status":
      return bridgeCall("GET", "/features/status");

    case "get_debug_journal":
      return bridgeCall("GET", "/debug/journal");

    case "save_debug_journal":
      return bridgeCall("POST", "/debug/journal", {
        hypothesis:        args.hypothesis,
        focusZones:        args.focusZones,
        skipZones:         args.skipZones,
        confirmedFindings: args.confirmedFindings,
        nextSessionPlan:   args.nextSessionPlan,
      });

    case "expand_variable": {
      const params = new URLSearchParams({ expression: args.expression });
      if (args.maxDepth != null) params.set("maxDepth", String(Math.min(args.maxDepth, 5)));
      if (args.frameIndex != null) params.set("frameIndex", String(args.frameIndex));
      return bridgeCall("GET", `/debug/variable/expand?${params}`);
    }

    case "get_exception_info":
      return bridgeCall("GET", "/debug/exception");

    case "list_threads":
      return bridgeCall("GET", "/debug/threads");

    case "switch_thread":
      return bridgeCall("POST", "/debug/threads/switch", { threadId: args.threadId });

    case "add_watch":
      return bridgeCall("POST", "/debug/watches", { name: args.name, expression: args.expression });

    case "get_watches":
      return bridgeCall("GET", "/debug/watches");

    case "remove_watch":
      return bridgeCall("DELETE", "/debug/watches", { name: args.name });

    case "pin_variable":
      return bridgeCall("POST", "/debug/pins", { name: args.name, expression: args.expression });

    case "get_pinned_variables":
      return bridgeCall("GET", "/debug/pins");

    case "unpin_variable":
      return bridgeCall("DELETE", "/debug/pins", { name: args.name });

    case "set_active_frame":
      return bridgeCall("POST", "/debug/frame/set", { frameIndex: args.frameIndex });

    case "get_vs_selection":
      return bridgeCall("GET", "/debug/selection");

    case "list_vs_instances": {
      const instances = discoverInstances();
      if (args.selectPid != null) {
        const match = instances.find((i) => i.pid === args.selectPid);
        if (!match) {
          throw new Error(
            `No running instance with PID ${args.selectPid}. Current instances: ` +
              JSON.stringify(instances)
          );
        }
        selectedBridgeUrl = `http://localhost:${match.port}`;
        return { instances, selected: match };
      }
      let currentlyResolved;
      try {
        currentlyResolved = resolveBridgeBaseUrl();
      } catch {
        currentlyResolved = null; // ambiguous — caller should pass selectPid
      }
      return { instances, currentlyResolved };
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

// ---------------------------------------------------------------------------
// MCP server wiring
// ---------------------------------------------------------------------------

const server = new Server(
  { name: "vs-debugger", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: TOOLS,
}));

server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const { name, arguments: args } = req.params;
  try {
    const result = await dispatchTool(name, args);
    return {
      content: [
        {
          type: "text",
          text:
            typeof result === "string"
              ? result
              : JSON.stringify(result, null, 2),
        },
      ],
    };
  } catch (err) {
    return {
      isError: true,
      content: [{ type: "text", text: `Error: ${err.message}` }],
    };
  }
});

// ---------------------------------------------------------------------------
// Startup
// ---------------------------------------------------------------------------

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  // MCP servers communicate over stdio — log to stderr so it doesn't interfere.
  let target;
  try {
    target = resolveBridgeBaseUrl();
  } catch (err) {
    target = `unresolved — ${err.message}`;
  }
  console.error(`vs-debugger MCP server ready (bridge target: ${target})`);
}

main().catch((err) => {
  console.error("Fatal:", err);
  process.exit(1);
});
