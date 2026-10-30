# Prompt Guide — Using vs-debugger Effectively

This is a guide to *how to ask* Claude Code to use the live Visual Studio
debugger, organized by scenario. The tools exist and work — getting good
results is mostly about being explicit about *which debugging technique* you
want, since Claude will otherwise default to the cheapest path (often just
reading the source statically, or jumping straight to an exception) rather
than the one you actually need.

For setup instructions, see [SETUP.md](SETUP.md).

---

## The tool surface, briefly

| Tool | What it does |
|---|---|
| `set_breakpoint` / `remove_breakpoint` / `list_breakpoints` | Manage line breakpoints |
| `start_debug` | F5-equivalent — launches the currently open solution |
| `attach_process` | Attach to a running process, local or remote (`remoteServer` param) |
| `continue_execution` | F5-equivalent while paused |
| `step_into` / `step_over` / `step_out` | F11 / F10 / Shift+F11 |
| `stop_debug` | Shift+F5-equivalent |
| `get_callstack` / `get_locals` / `evaluate_expression` | Inspect state while paused |
| `set_break_on_exceptions` | Pause on **any** thrown exception, even if caught later |
| `wait_for_debugger_change` | Block until something happens, instead of polling (now also bundles up to 15 top-frame locals automatically) |
| `list_vs_instances` | List/select between multiple simultaneously-running VS instances |
| `set_output_streaming` / `list_output_panes` / `get_output_snapshot` / `wait_for_output` | Capture and read Output/Debug window text (Console.WriteLine, etc.) — off by default |
| `set_module_diagnostics` / `get_loaded_modules` | List loaded modules with attached/symbol status — off by default, for "stepping isn't working" investigations |
| `get_control_mode` | Read-only check of the current collaboration mode (driver/observer/paused/user-driving/emergency-stop) — Claude can check this but never change it; only the user can, via Visual Studio's Debug > Claude Debug Control menu |
| `get_debug_event_log` | Full history of transitions since a version, not just the latest one — use when fast manual stepping might have skipped intermediate breakpoints between `wait_for_debugger_change` calls |
| `get_step_filters` | List all lines the user has marked "step over for Claude" — normally not needed since `wait_for_debugger_change` already includes `suggestedStep` per line; useful to review what's marked or diagnose unexpected step_over behavior |
| `get_feature_status` | Check which features are enabled or disabled for this session — features auto-disable if they hit an access/permission error (IT policy); call this at session start or when a tool returns HTTP 503 |
| `get_debug_journal` | Read the persisted cross-session debug journal — hypothesis, focusZones, skipZones, confirmedFindings, nextSessionPlan, and auto-saved lastKnownState from the last breakpoint; ALWAYS call this before setting any breakpoints |
| `save_debug_journal` | Write your analysis mid-session — hypothesis, focusZones to inspect carefully, skipZones confirmed safe, confirmedFindings, nextSessionPlan; bridge auto-saves position+locals on every breakpoint so call this for your interpretation |

---

## Scenario 1: "Something's broken, find out why" (the default case)

This is the most common real ask: you don't know what's wrong or where.

> Run [project], and figure out why it's failing — don't just guess from
> reading the code, actually use the live debugger to inspect what's
> happening at the point of failure.

**The one phrase that matters most: "don't just guess from reading the
code."** Claude is often capable of spotting a bug just by reading source,
which technically "solves" your question but never exercises the debugger
tools at all. If your goal is to validate the debugging pipeline (not just
get an answer), always include this instruction.

### If the bug is silently caught and logged (not a real crash)

If your code wraps risky calls in `try/catch` and just logs the error (very
common in real batch-processing code), the debugger will **never** pause on
its own — VS only auto-breaks on exceptions that are truly unhandled
anywhere on the stack. Say so explicitly:

> The errors are caught and logged, not crashes — enable break-on-exceptions
> first, so you catch them live at the actual throw site instead of just
> reading the printed log message afterward.

---

## Scenario 2: Genuine line-by-line stepping

This is the "classic" debugging workflow most engineers actually use day to
day — set a breakpoint, then step through one line at a time, watching
values change. Left to its own devices, Claude will more often jump
breakpoint-to-breakpoint or exception-to-exception, which is faster but
skips the line-by-line narrative.

> Set a breakpoint at [file:line]. Start debugging, and once it hits, step
> through one line at a time. After each step, show me the ACTUAL runtime
> values of local variables from the debugger — don't read the source file
> to describe what the line says, show what the variables actually contain.
> If a variable holds an object or the value isn't clear from locals, call
> evaluate_expression to get the real value. Keep going until you reach the
> actual bug, then explain what's wrong based on what you observed at runtime.

The critical phrase here is **"actual runtime values… don't read the source file"**.
Without it, Claude defaults to the cheapest path: reading the `.cs` file and
describing the syntax ("line 66 is `var processor = new AccountProcessor(...)`")
instead of calling get_locals or evaluate_expression to show real values
(`processor = null`, `customerProfiles.Count = 3`, etc.). The source description
looks plausible but tells you nothing about what's actually in memory at that
moment — which is exactly what the debugger is for.

Key phrases doing the work here:
- **"actual runtime values from the debugger"** — forces use of `state.locals` from `wait_for_debugger_change` or a follow-up `get_locals` call; blocks the file-read shortcut.
- **"evaluate_expression to get the real value"** — for objects, collections, or anything where the locals summary isn't enough (e.g. `processor.Accounts[0].Balance`).
- **"one line at a time"** — when you say "step into" or "debug line by line", Claude will use `step_into` by default. If some lines are marked with the teal `SO` gutter icon (see Scenario 13), Claude automatically uses `step_over` there.
- **"tell me what changed"** — makes it narrate, rather than silently loop.

---

## Scenario 3: Multiple different bugs/exception types in one run

If you suspect several distinct problems (not just one), say so — otherwise
Claude may stop investigating after finding the first plausible cause.

> Run the batch and figure out why several different accounts are failing —
> there are multiple distinct bugs here, not just one shared cause. Use
> break-on-exceptions and trace each failure individually with the live call
> stack and locals, then give me the precise root cause and fix for each one.

---

## Scenario 4: Remote debugging (msvsmon)

Be explicit about whether the process is already running (needs
`attach_process`) or needs to be launched (`start_debug`) — guessing wrong
here wastes a turn.

> A debugger is already attached to [process.exe] (remote server
> `host:port`) — don't attach again. Enable break-on-exceptions, then tell
> me when you're ready so I can [trigger the run]. Then debug live and tell
> me the root cause of each failure.

If it hasn't been attached yet:

> Attach to [process.exe] remotely on `host:port` — it's already running and
> waiting at a console prompt. Enable break-on-exceptions, then tell me when
> you're ready so I can press Enter to start the batch.

---

## Scenario 5: Code that lives outside the loaded solution

On large solutions (PSGTMS-scale, 100+ projects), only a subset is typically
loaded at once. If a bug might be in a project you *haven't* loaded, say so —
this nudges Claude to actually check the `attached` field on stack frames
rather than assume every frame is fully inspectable.

> The bug might be in a project that isn't currently loaded in this VS
> solution. When you inspect the call stack, check whether each frame is
> part of the loaded solution — if not, tell me that explicitly rather than
> assuming you can't make progress; the breakpoint/exception location is
> still useful even without full symbols.

`get_callstack` and `wait_for_debugger_change` both return `attached: false`
plus an explanatory note for frames outside the loaded solution — this
applies equally to your own unloaded projects and to pure BCL/framework code
(e.g. `mscorlib.dll`).

---

## Scenario 6: Re-running after a fix, without restarting

If your test harness loops and re-prompts (see the `TestApplication` pattern
in this repo — runs a batch, then asks "press Enter to run again, or type
'exit' to quit"), say so, so Claude doesn't unnecessarily reattach or relaunch:

> The process is still running and will loop back to a prompt after this
> batch finishes — once you're done with this pass, just tell me, and I'll
> trigger the next run from the same process. No need to restart or
> reattach.

---

## Scenario 7: Multiple VS instances running at once

If you have more than one Visual Studio window open (e.g. debugging two
different solutions, or a normal instance plus an Experimental Instance),
any tool call can come back with an "ambiguous, multiple instances found"
error. This isn't a failure — it's the system correctly refusing to guess.

> There might be more than one Visual Studio instance running. List the
> available instances and pick the one with solution [name] before doing
> anything else.

This nudges Claude toward calling `list_vs_instances` first and selecting
by PID, rather than retrying blindly or assuming the first error was a
fluke. Once selected, the choice sticks for the rest of that Claude Code
session — no need to repeat this on every subsequent call.

If you already know there's only one instance and just want to confirm
which solution it's pointed at (useful after switching projects):

> Confirm which Visual Studio instance and solution you're currently
> talking to before we start.

---

## Scenario 8: Reading console output instead of just stepping

If you need to see what the program is printing (logs, trace lines) rather
than (or alongside) stepping through code, say so explicitly — output
streaming is off by default specifically so it isn't burning tokens on
every session.

> Enable output streaming on the Debug pane, then run the batch and tell
> me what gets printed, alongside any breakpoints you hit.

Turn it off again once you're done with that specific investigation —
leaving it on has no extra dollar cost while idle, but no point keeping
it enabled past the task that needed it.

## Scenario 9: "Stepping isn't working" / suspected missing symbols

If breakpoints aren't binding, stepping silently does nothing, or you're
debugging a remote (msvsmon) target and suspect a PDB/source mismatch,
ask for module diagnostics specifically — don't expect Claude to reach
for this proactively, since it's off by default to avoid dumping 50+
modules on unrelated investigations.

> Stepping isn't landing where I expect. Enable module diagnostics and
> check whether the current module has symbols loaded and whether it's
> part of the loaded solution.

## Scenario 10: A tool call fails with HTTP 423

If Claude reports a tool call blocked with a 423 error, that means the user
has switched Visual Studio out of "driver" mode via the Debug > Claude
Debug Control menu — Claude has no way to undo this itself, by design.

> A tool call just failed with a 423 error. Check get_control_mode and
> tell me what mode we're in and what I need to do in the VS UI to let
> you continue.

If you (the user) didn't intentionally switch modes, check Debug > Claude
Debug Control in Visual Studio — the checkmark shows which mode is active.

## Scenario 11: Fast manual stepping causes missed transitions

If you drive several breakpoints/steps quickly in the VS UI while Claude
is busy (writing analysis, calling another tool), `wait_for_debugger_change`
only ever returns the latest state — intermediate pauses silently
disappear, since that tool is "what's true right now," not a history.

> I just stepped through several breakpoints quickly. Check
> get_debug_event_log to see everything that happened, not just where we
> ended up.

This is also worth saying proactively before a fast manual-driving burst,
so Claude knows to reconcile with the event log afterward rather than
trusting only its last wait_for_debugger_change snapshot:

> I'm going to continue through a few breakpoints quickly — once I'm
> done, pull the full event log instead of just the current state, so
> you don't miss anything in between.

## Scenario 12: You're driving manually, Claude just watches and reports

This is the full "observer mode" workflow in one prompt — covers the
passive watch loop, what to do if you hand control back mid-session, and
when to actually stop. Earlier sessions found that without all three
pieces spelled out up front, Claude either stops to ask a question after
the first interesting thing it sees (breaking the "just watch" intent),
or sits idle forever after you flip its mode back to driver because it
never notices the change. Both are fixed by stating all three pieces in
one prompt instead of patching them in one at a time mid-session:

> Stay in observer mode for now — just watch and report what you see as
> I step through manually. Loop on wait_for_debugger_change with a
> 60-second timeout and keep going automatically; don't stop to ask me
> anything in between.
>
> Log each transition briefly (function, exception if any, account if
> relevant) but don't pause for confirmation — just keep watching.
>
> If you ever see an event type of ControlModeChanged, that means I
> switched your mode via the VS Debug menu — check get_control_mode
> immediately. If I've switched you to driver mode, stop just watching
> and start actually driving (continue/step as needed) instead.
>
> Stop and summarize everything you observed only when: (1) I explicitly
> tell you to stop, or (2) the process exits/finishes on its own.

Why each piece matters:
- **"Loop... keep going automatically; don't stop to ask"** — without this, Claude treats finding something interesting (e.g. an exception) as a natural stopping point and asks what to do next, ending the watch loop.
- **"check get_control_mode... start actually driving instead"** — without this, Claude has no instruction to act differently once you hand control back; it'll keep passively watching even in driver mode unless told the mode change is a cue to change behavior.
- **Two explicit stop conditions** — removes any ambiguity about when the loop should end, so Claude isn't guessing whether "the batch is probably done" on its own.

## Scenario 13: Marking specific lines as "step over" instead of "step into"

By default, Claude uses `step_into` for line-by-line debugging — it dives into
every function call on each line. This is correct behavior most of the time, but
becomes wasteful for utility/helper calls you already understand and don't need
to trace through (logging calls, null-guards, string formatters, etc.).

**How to mark a line:**

Right-click any line in the code editor → **"Mark as Step Over for Claude"**.
The line gets a teal `SO` circle in the gutter (same margin as breakpoints, but
teal not red). Right-click again → **"Unmark Step Over for Claude"** to remove it.

Markers persist per-solution in a `.claude-step-filters.json` file next to the
`.sln` — they reload automatically when you reopen the solution.

**How Claude uses them:**

`wait_for_debugger_change` now includes two new fields in its response when paused:
- `state.file` / `state.line` — the current execution position
- `state.suggestedStep` — `"stepInto"` or `"stepOver"` based on whether that line is marked

Claude should always respect `suggestedStep` rather than defaulting to `step_into`.
You don't need to say anything — the marker in the file does the talking.

**Prompting only needed if you want Claude to check marks proactively at the start:**

> Before we start stepping, check get_step_filters so you know which lines I've
> already marked as step-over — then step_into everything except those.

Normally this isn't needed; the `suggestedStep` field in each `wait_for_debugger_change`
response carries the same information on a per-step basis.

## Scenario 14: Starting a second debug session smarter (debug journal)

After the first session you already know what's safe to skip and where the
problem likely lives. The journal carries that forward automatically.

**How to start session 2:**

> Before setting any breakpoints, call get_debug_journal. If a journal exists,
> read the nextSessionPlan and focusZones — set breakpoints at the focusZone
> entry lines, use continue_execution to skip past skipZones, and evaluate the
> watchExpressions immediately when you land. Show me the actual runtime values
> at those points. Update the journal mid-session whenever you confirm or rule
> out something — don't wait until the end.

**How to save findings mid-session (don't wait for the end):**

> You just ruled out that the constructor is the problem. Save that to the
> journal now — mark lines 95-101 as a skipZone and update the hypothesis.

The bridge auto-saves `lastKnownState` (position + locals) on every breakpoint
hit and when the debug session ends — so even if VS crashes or Claude is
stopped, the last known position is never lost. `save_debug_journal` is for
Claude's interpretation layer on top of that raw state.

**Chunked log review (avoiding memory size issues):**

When reviewing the event log across a long session, pull in chunks rather than
all at once:

> Pull the debug event log in chunks of 50 starting from sinceVersion 0.
> Summarize each chunk into the journal (confirmedFindings) before pulling
> the next chunk — don't try to hold the whole log in context at once.

## Scenario 15: A feature is disabled (access/permission error)

If a tool returns HTTP 503, it means that specific feature auto-disabled itself
because it hit an access restriction on this machine — IT policy, file system
permissions, or a restricted COM API. The rest of the debugger keeps working.

> A tool just returned 503. Call get_feature_status, tell me which feature is
> disabled and why, and explain what still works so I know what I can use.

What Claude should say when a feature is disabled:

- "Step filter saving is disabled (file write blocked). Your marks work this
  session but won't reload next time VS opens. The debugger itself is fine."
- "Debug journal saving is disabled (no write access to solution folder).
  I'll keep notes in this conversation instead, but they won't persist."
- "Output streaming is disabled (COM access denied). I can't read the Debug
  Output pane this session, but breakpoints, stepping, and locals all work."

Never say "the debugger is broken" — only that specific feature is off.

## Anti-patterns (things that quietly produce weak results)

- **Not naming a specific tool/technique** when you actually care about
  *how* the debugging happens (line-by-line vs. exception-jumping vs. static
  read) — Claude will pick whichever is fastest, which is rarely the one you
  wanted if you don't say so.
- **Forgetting to mention break-on-exceptions is needed** when bugs are
  caught-and-logged — without it, the debugger simply never pauses, and
  Claude has nothing live to inspect.
- **Vague timing instructions** like "debug this" with a process that's
  about to finish in milliseconds — if there's a race between attach and
  execution, say explicitly that the process is waiting for input/already
  paused, so Claude doesn't assume it has time it doesn't.
- **Not mentioning "multiple bugs"** when there are several — otherwise
  Claude may report the first one found as if it were the whole answer.
- **Not saying "show actual runtime values"** during line-by-line stepping —
  Claude defaults to reading the source file and describing syntax
  (`"line 66 is var processor = new AccountProcessor(...)"`) instead of
  calling get_locals/evaluate_expression to show what's actually in memory
  (`processor = null`, `accounts.Count = 0`, etc.). The source description
  looks reasonable but tells you nothing useful — always say "show actual
  values from the debugger, don't read the source file."
