# Features & Open Items

This file holds:
1. The **collaboration model** — the main design for how user and Claude
   share a live debug session (this is the substantive part).
2. **Integration questions** with the user's existing MCP implementation.
3. **Future ideas** not yet planned.
4. **Out of scope** items, locked in.

When an item here is committed to, it gets promoted into `PLAN.md` as a
numbered phase. Until then, this is the design surface.

---

# The Collaboration Model (planned as Phase 4.5)

## Framing

The first instinct when planning "context menu + buttons" was to add three
controls: Take Control / Hand to Claude / Pause Claude. That's mechanically
correct but operationally thin, because real debugging isn't binary
"me OR Claude." It's a fluid back-and-forth where the *kind* of handoff
matters more than the toggle.

**Core insight:** debugging is collaborative investigation, not command
execution. The features should support real pair-programming patterns —
driver/navigator, rubber-duck observation, structured handoffs, post-session
review. The "extension" isn't automation with an off-switch; it's a
structured collaboration channel between human and AI inside a live debug
session.

## Real-time scenarios driving the design

These are the actual situations the model has to handle well. Each scenario
maps to one or more features below.

| # | Scenario | Need |
|---|---|---|
| 1 | "Claude, find me where this bug starts." Claude attaches, sets BPs, steps, inspects. Mid-flow user notices something Claude missed and wants to look around without losing Claude's investigation state. | Soft pause that preserves session + Claude's context |
| 2 | "I'm driving, but Claude — watch and tell me what you see." User steps manually; Claude reads everything, comments, asks questions, but can't click anything. | Observer mode (read-only Claude, still in the loop) |
| 3 | "Claude, set up the scenario, then hand off to me." Claude does the boring prep (attach, BPs, navigation to interesting state) then explicitly hands off for the actual investigation. | Claude-initiated handoff *request*, user confirms |
| 4 | "Whoa, stop everything." Claude is going wrong direction — runaway loop, wrong process, about to clobber state. One click, everything stops. | Emergency stop, distinct from pause, requires explicit re-arm |
| 5 | "Show me what Claude is about to do." Before irreversible operations (detach, mass clear, stop session) the user wants a confirmation step. | Per-action confirmation gate, configurable + persisted |
| 6 | "What was Claude doing for the last 5 minutes?" User stepped away, comes back to a long history of Claude actions and needs to see the trail without scrolling chat. | Timeline panel inside VS, timestamped, clickable, current-state visible |
| 7 | "Take us back to where we were 10 minutes ago." Long debug session where user wants to restore a known good investigation state. | Session bookmarks (state snapshot + restore) |

## The five capabilities

### 1. Multi-state control model

Not binary. Five modes, each with explicit semantics. The bridge holds a
single `controlMode` enum; the MCP-facing endpoints behave differently
depending on the mode.

| Mode | Mutating MCP calls | Read-only MCP calls | Visible to Claude? |
|---|---|---|---|
| `driver` | Allowed | Allowed | Yes — Claude knows it's driving |
| `observer` | Rejected (HTTP 423 with explanatory body) | Allowed | Yes — Claude knows it's observing only |
| `paused` | Rejected (HTTP 423) | Allowed | Yes — Claude knows it's paused, can still reason from current state |
| `user-driving` | Rejected (HTTP 423) | Allowed | Yes — Claude knows user is driving |
| `emergency-stop` | Rejected (HTTP 423) | Rejected (HTTP 423) | Yes — Claude blocked entirely, must be re-armed by user |

Why "observer" is separate from "paused": in paused, Claude shouldn't be
generating analysis ("I'd suggest stepping into X"); in observer mode, that
IS the value. Same HTTP behavior, different signal to Claude about how to
behave conversationally.

Why "emergency-stop" is separate from "user-driving": user-driving is normal
collaborative handoff; emergency-stop is "something went wrong, halt." It
requires explicit re-arm to leave, so the user has to consciously re-engage
Claude rather than just clicking away.

The bridge surfaces the current mode on every response (header or body) so
the MCP server and Claude Code both know the current state without polling.

### 2. Handoff requests (bidirectional, but asymmetric)

User → Claude is **instant and unconditional.** User is always trusted; no
confirmation, no negotiation. Click "Hand to Claude," done.

Claude → User is **a request, not a take.** Claude calls a new MCP tool:

```
request_user_handoff(reason: string, suggestedAction?: string)
```

Bridge surfaces this as a **non-modal notification** in VS:

> 🤖 Claude is ready to hand off
> "I've set 4 breakpoints in BatchMaintenance and attached to w3wp on
> staging. Ready for you to investigate the loop in
> ProcessBatch.cs:142."
> [Accept] [Decline] [Snooze 5min]

If accepted, mode flips to `user-driving`. If declined, Claude stays in
`driver` and is told its handoff was declined (so it can keep going or ask
why). If snoozed, the notification reappears later.

Why this matters: lets Claude end its turn cleanly at a meaningful break
point ("I've done the setup, your turn to investigate") instead of either
running until something breaks or stopping mid-task with no context.

**Security note:** this is a *request* mechanism, not a control-stealing
mechanism. Claude can never set the mode itself; it can only ask. The user
remains the only entity that can change `controlMode`. The /control endpoint
that mutates state is callable only from inside the VS process, never from
MCP.

### 3. Confirmation gate for destructive actions

Some MCP calls have outsized blast radius:

- `stop_debug` — kills the session entirely; if the session was hard-won
  (msvsmon credentials, finding the right remote process, getting to the
  right state), this is painful to redo
- `detach` (via stop_debug variant) — same
- Mass breakpoint clear (would need a new MCP tool, but worth designing for)
- `attach_process` to a new target while already attached — implies
  detaching the current session

When Claude calls one of these, the bridge surfaces a VS dialog:

> ⚠ Claude wants to: stop the current debug session
>
> Context: attached to PSGTMS.WebHost on stg-app-01, 4 breakpoints set
>
> [Allow once] [Allow for this session] [Deny] [Configure]

Per-session "Allow for this session" decisions are remembered until VS
closes. The "Configure" option opens a settings page where the user can
mark certain operations as always-allowed (e.g. "I don't care about
confirmations for `stop_debug`, always allow it").

Default-on list (sensible defaults user can override):
- `stop_debug`
- `attach_process` when already attached to something

Default-off list (rare enough or reversible enough not to gate):
- `set_breakpoint` / `remove_breakpoint` / `list_breakpoints`
- All step/continue/inspect/evaluate operations

### 4. Activity timeline panel (VS Tool Window)

A dedicated panel in VS — register a `ToolWindowPane` so it docks like
Solution Explorer or Output. Always visible, always current.

**Content:**

| Time | Mode | Action | Args | Result |
|---|---|---|---|---|
| 14:23:12 | driver | set_breakpoint | ProcessBatch.cs:142 | ✓ id=bp-1 |
| 14:23:18 | driver | attach_process | w3wp.exe @ stg-app-01 | ✓ pid=4128 |
| 14:23:25 | driver | get_callstack | — | 12 frames (click to expand) |
| 14:23:30 | **user-driving** | (user took control) | — | — |
| 14:24:01 | observer | get_locals | frame 0 | 8 locals (click to expand) |

**Interactions:**
- Click action row → expands to show full args/result
- Click file-line in args (e.g. `ProcessBatch.cs:142`) → jump VS editor to that line
- "Replay" button on read-only actions → re-run same `get_locals` etc., see if state changed
- Filter by mode (show only `driver` actions, or only mode-change events)
- Top of panel: large mode indicator with colored dot (driver=green, observer=blue, paused=yellow, user-driving=purple, emergency=red)
- "Clear log" button — soft-clears the view; the log persists to disk regardless

**Persistence:** all actions written to a per-session JSON log file in
`%LOCALAPPDATA%\VSDebugBridge\sessions\<timestamp>.json`. Useful for post-mortem
("what did Claude do during yesterday's investigation?"). Older logs
auto-rotate after N days.

Why this matters: solves the "I stepped away for coffee and now there are
47 lines of chat" problem. Visibility into Claude's actions, decoupled
from Claude Code's chat UI.

### 5. Session bookmarks

A "snapshot" mechanism for debug-investigation state. User clicks "Mark
this point" or types a name; bridge captures:

- Current process attached (if any)
- Open breakpoints (file:line + conditions)
- Watch expressions
- Current frame / call stack (best-effort — can't restore stack, but
  can restore "where we were investigating")
- A user-supplied label and optional note

Stored alongside the activity log. Listed in a small dropdown on the
timeline panel.

**Restore semantics:** "Take us back to bookmark X" — bridge re-attaches
to the named process (with a fresh credential prompt if needed), restores
all breakpoints, re-runs the program to a state matching the bookmark
where possible. Cannot restore actual program execution state (would need
time-travel debugging which msvsmon doesn't support) — but it gets you
back to "the breakpoints and watches set up the same way."

Two main use cases:
- **Pre-handoff bookmark:** Claude marks "before I start investigating X,"
  user can return to a known good state if Claude's investigation goes
  sideways
- **End-of-day bookmark:** user marks where they stopped before logout;
  resumes tomorrow with one click

Bookmarks are also useful even without restore — just as a chronological
list of "interesting moments" with notes.

## Phasing within Phase 4.5

These are independently useful, so they ship in stages. Each one is a real
deliverable, not a half-feature.

| Phase | Scope | Effort | Value |
|---|---|---|---|
| 4.5a | Multi-state control model + emergency stop. **IMPLEMENTED** (see below). | ~0.5 day | The safety floor — can't do anything else safely without this |
| 4.5b | Handoff requests + confirmation gate. New `request_user_handoff` MCP tool, VS notifications, confirmation dialogs, per-session preferences. | ~1.5 days | The collaboration layer — turns automation into pairing |
| 4.5c | Activity timeline panel. ToolWindow, custom WPF list, log persistence to disk, clickable jumps. | ~1.5 days | The visibility layer — solves the "what did Claude do?" problem |
| 4.5d | Session bookmarks. Snapshot/restore logic, bookmark UI, named recall. | ~1 day | The convenience layer — significantly reduces handoff friction |

Total: ~4.5 days of focused work *after* Phase 2 compiles. Each phase is
shippable on its own; no need to do all four before getting value.

### 4.5a implementation notes (what shipped vs. what's deferred)

**Shipped:**
- `BridgeServer.ControlMode` enum (driver/observer/paused/user-driving/emergency-stop), default `driver`.
- `EnsureCallAllowed` gates every mutating endpoint with HTTP 423 when not in driver mode; emergency-stop blocks everything except `GET /control/mode`.
- `GET /control/mode` (read-only) + `get_control_mode` MCP tool — Claude can check the mode and explain to the user why a call was blocked, but has no path to change it.
- `ClaudeControlCommands.cs` + `VSCommandTable.vsct` — a **Debug > Claude Debug Control** menu with 5 commands (Let Claude Debug / Let Claude Watch Debug / Pause Claude Debug / Take Over Debug / Stop Claude Debug), each calling `BridgeServer.SetControlModeFromUi` as a direct in-process method call — never exposed over HTTP, so there is no path from MCP to mutate the mode. Checkmark shown next to whichever mode is currently active (`OleMenuCommand.Checked` via `BeforeQueryStatus`).
- Keyboard shortcuts: `Ctrl+Shift+Alt+1` (Let Claude Debug), `Ctrl+Shift+Alt+2` (Pause Claude Debug), `Ctrl+Shift+Alt+3` (Stop Claude Debug).
- **Code editor right-click context menu** also shows the same "Claude Debug Control" submenu (added after initial 4.5a — same 5 commands/handlers, just re-parented into a second `<Group>` under `IDM_VS_CTXT_CODEWIN` via `<CommandPlacements>`, no duplicated buttons or code).

### Bug found in real use: `wait_for_debugger_change` silently drops fast transitions — FIXED

**Found by live testing, not by inspection.** Reported behavior: when the
user drove several breakpoints/steps quickly in the VS UI, Claude's
`wait_for_debugger_change` calls only ever showed the LAST one — all
intermediate pauses were gone, with no indication anything was missed.
Slow stepping (pacing matched to Claude's polling) worked fine.

**Root cause:** `RecordChange` only ever tracked a single `_lastEventType`
field and a version counter — a "what's true right now" model, not a
history. Each new transition overwrote the previous one before
`wait_for_debugger_change` had a chance to be called again.

**Fix:** added a bounded ring buffer (`_eventLog`, cap 200 entries) — every
transition now appends a full structured `DebugLogEntry` (version,
eventType, mode, function, attached, exception, capped locals,
timestamp) instead of overwriting one slot. New endpoint
`GET /debug/eventlog?sinceVersion=N&maxEntries=M` returns every entry
since `sinceVersion` (most-recent-wins if over the cap), and a matching
`get_debug_event_log` MCP tool. `wait_for_debugger_change` itself is
unchanged — it's still "current state now"; the new tool is "everything
that happened since I last checked."

**Bonus, matching the user's own analogy to VS's Output window:** every
transition also gets written as a one-line human-readable summary to a
new dedicated **"Claude Debug Log"** Output pane (created lazily via
`OutputWindowPanes.Add`), so the user can watch the same transition
history scroll by live in VS, independent of whether Claude is polling.
Since it's a real Output pane, the already-built output-streaming
feature (`set_output_streaming`) can also capture it, for free.

### Bug found in real use: control-mode changes invisible to a waiting Claude — FIXED

**Found by live testing.** The user handed control back to Claude via the
Debug menu (switching from `observer` back to `driver`) while Claude was
mid-loop calling `wait_for_debugger_change` repeatedly per its own
instruction to "keep watching." Claude never noticed the mode change and
kept reporting "still idle" indefinitely.

**Root cause:** `SetControlModeFromUi` only set `_controlMode` — it never
touched `_changeVersion`/`_changeSignal`, so a pure mode flip (with no
actual debugger execution change) gave `wait_for_debugger_change` nothing
to wake up on. The polling loop was watching the wrong thing for this
event.

**Fix:** `SetControlModeFromUi` now calls the same `RecordChange` path
debugger transitions use, tagged `"ControlModeChanged:<mode>"`. This
means a mode switch via the VS menu: (1) immediately wakes any in-progress
`wait_for_debugger_change` call, (2) shows up in the Claude Debug Log
pane, (3) is visible via `get_debug_event_log`. Tool descriptions updated
so Claude knows to call `get_control_mode` and change its own behavior
(e.g. start actually driving again) when it sees this event type, rather
than just logging it and continuing to passively wait.

### Follow-up bug found in real use: the fix above was masked by an older shortcut — FIXED

**Found immediately after testing the previous fix.** The control-mode
change WAS being recorded correctly (version advanced right on schedule),
but `wait_for_debugger_change` still never reported it. Claude only found
out because it proactively re-checked `get_control_mode` on its own
initiative — the wait tool itself stayed silent about the real cause.

**Root cause:** `WaitForChangeAsync`'s "debugger already paused" shortcut
(added earlier, for a different reason — avoid burning the full timeout
when the answer is already sitting there) **unconditionally hardcoded**
`eventType` to `"alreadyInState:<mode>"` on every single call while the
debugger stayed paused, regardless of whether something genuinely new
(like a `ControlModeChanged` event) had happened since the caller's
`sinceVersion`. Since the debugger sat paused for an extended
observer-mode session, every call hit this shortcut and the real event
type was silently overwritten, every time.

**Fix:** the shortcut now only falls back to the generic
`"alreadyInState:<mode>"` label when nothing has happened since
`sinceVersion` (i.e. the caller already knew about this exact state). If
something genuinely new occurred — any real event, not just
`ControlModeChanged` — it reports that instead.

### Second follow-up bug, found from raw event-log evidence: the fix above still didn't cover the no-baseline case — FIXED

**Found by inspecting the Claude Debug Log pane directly** — the log
itself correctly recorded distinct `ControlModeChanged:driver`/`:observer`
entries, proving `RecordChange` was working. But Claude's chat transcript
still showed "still idle" responses spanning that exact window, meaning
`wait_for_debugger_change` still wasn't surfacing them.

**Root cause:** the first fix's condition was
`sinceVersion >= 0 && sinceVersion < currentVersion` to decide "something
new happened." But callers frequently omit `sinceVersion` entirely (the
bridge defaults it to `-1`, meaning "no baseline known") — and `-1` fails
the `>= 0` check, so the condition was always false whenever a caller
didn't happen to pass one, silently falling back to the generic label
exactly like before the first fix.

**Fix:** inverted the logic to require proof the caller is already
caught up, rather than proof something is new — `sinceVersion >= 0 &&
sinceVersion >= currentVersion`. Any other case (no baseline given, or a
stale baseline) now correctly reports the real most recent event instead
of defaulting to "nothing new."

**Deferred (not in this pass):**
- **Separate toolbar button group.** Only the Debug-menu submenu was built. A standalone toolbar is a larger, more error-prone VSCT addition (new `<Toolbars>`/`<Bitmaps>` definitions) — the menu alone already delivers the actual safety mechanism.
- **Clickable status-bar mode picker.** No status bar UI was added at all in this pass; the Debug menu's checkmark is the only visible "what mode am I in" indicator for now.
- **Separate "Resume Claude Debug" label.** "Let Claude Debug" doubles as the re-arm action from `emergency-stop` (both just set mode to `driver`) rather than a 6th button with dynamic slot-relabeling — functionally identical, simpler to implement correctly.
- **Live-tested only by compiling, not yet by clicking through in a running VS session** — build succeeds and the VSCT resource (`VSCommandTable.cto`) is confirmed compiled into the DLL, but the actual menu/checkmark/shortcut behavior hasn't been clicked through in a live VS window yet.

## Out-of-scope clarifications for the collaboration model

- **Claude cannot set `controlMode` itself.** Only the user can, through VS
  UI. Claude can *request* via `request_user_handoff` but cannot mutate.
- **Emergency stop requires explicit user re-arm.** No timeout, no
  "auto-resume after N seconds." Must be a deliberate click.
- **Bookmarks don't restore program execution state.** Can't rewind a
  running process. Only debugger configuration is restored.
- **Activity log is local-only.** Not synced anywhere. No telemetry to
  Anthropic or anywhere else.

## UI naming (locked in)

Debug menu submenu name: **Claude Debug Control** (under the existing
Debug menu). Toolbar group label: **Claude Control**.

| Mode | Menu/toolbar label | Status bar text |
|---|---|---|
| `driver` | **Let Claude Debug** | 🟢 Claude Driving |
| `observer` | **Let Claude Watch Debug** | 🔵 Observing |
| `paused` | **Pause Claude Debug** | 🟡 Paused |
| `user-driving` | **Take Over Debug** | 🟣 You're Driving |
| `emergency-stop` | **Stop Claude Debug** | 🔴 Stopped |
| re-arm (only shown while in `emergency-stop`) | **Resume Claude Debug** | — |

The re-arm label replaces "Stop Claude Debug" in that menu slot while
already stopped, so the slot always shows the *next* available action
rather than a confusing already-true state.

### Handoff-related labels (4.5b)

| Action | Label |
|---|---|
| Accept a Claude-initiated handoff | **Accept Handoff** |
| Decline | **Decline (Keep Driving)** |
| Snooze | **Snooze 5 min** |
| User proactively asks Claude to wrap up | **Ask Claude to Hand Off** |

### Bookmark-related labels (4.5d)

| Action | Label |
|---|---|
| Create | **Bookmark This Point** |
| Open list | **Debug Bookmarks...** |
| Restore | **Restore Bookmark** (submenu lists names) |

## Design decisions (locked in)

- **Mode buttons live in VS UI:** both a toolbar group AND a Debug menu
  submenu, plus keyboard shortcuts for all five mode transitions.
- **Status bar mode indicator is clickable, but opens a dropdown picker**
  listing the five modes — it does NOT instantly cycle/toggle on a single
  click. This keeps quick access (one click away) while avoiding an
  accidental mode flip mid-debug session (e.g. fat-fingering the status
  bar and silently locking Claude out of mutating calls via `observer`,
  or worse, jumping to `emergency-stop`). Same UX pattern VS already uses
  for the encoding/line-ending status bar item.
- **Bookmark scope: per-solution.** The dropdown only shows bookmarks
  created while debugging the currently open solution — no global view,
  no toggle. Keeps the list relevant on large multi-solution workflows
  without added complexity.
- **Handoff notification UX: both channels.** A native Windows toast
  (visible even if VS isn't the focused window) AND an in-VS banner
  with the actual [Accept] [Decline] [Snooze 5min] buttons (only the
  in-VS banner carries the interactive controls — the toast is purely
  an attention-getter).

---

# Integration with user's existing MCP implementation

**Status:** resolved. This was written when the bridge assumed a single
hardcoded port and a single VS instance — both have since been built and
shipped (see `BridgeServer.cs` `Start(basePort, maxAttempts)` and
`index.js` `discoverInstances()`/`list_vs_instances`), which closes the
two high-risk collision points below. Kept for history; no action needed.

## Collision points (as originally assessed)

| Layer | Risk (then) | Current status |
|---|---|---|
| MCP registration name (`vs-debugger`) | High | Still a real consideration if a second, unrelated MCP server reuses the same name — `claude mcp list` reveals current state. |
| Port `localhost:5959` | High | **Resolved.** Each bridge instance now auto-tries `basePort..basePort+9` and binds whichever is free; no longer a single point of collision. |
| VSIX GUID (`8B5C6E1A-3D2F-4A7C-9F8E-1B6D4A2E5C90`) | Low-medium | Unchanged — still only a concern if a second, unrelated VSIX reuses the same GUID. |
| Tool names (`set_breakpoint`, etc.) | Low | Unchanged — low risk by design, different MCP servers don't collide on tool names. |

## Decision tree (kept for history — applies only if a genuinely separate implementation appears)

1. **User built only the MCP server side, no VSIX bridge yet** → easiest.
   Compare and merge whichever is better; drop the other.
2. **User built both MCP server AND a VSIX bridge** → check if architecture
   is similar (HTTP, same endpoints) or different (e.g. named pipes). If
   similar, diff and merge. If different, pick one approach and commit;
   archive the other.
3. **User built something completely different** (DAP-based, different
   protocol) → no collision because nothing overlaps; treat as two separate
   projects with distinct names.

---

# Future ideas (not yet planned)

Loose collection — promote to `PLAN.md` when ready.

### Auto-attach by solution heuristic
When user opens a known solution (e.g. PSGTMS), MCP server could auto-suggest
the matching remote target rather than waiting for explicit instruction.
Risk: fragile if solution paths change. Worth doing only after Phase 4 is
stable.

### Saved debug "recipes"
Bundle "attach to staging w3wp + set these 5 breakpoints + watch these
expressions" as a named action. Live as JSON files in `.claude/`. Useful
for recurring PSGTMS patterns (e.g. "investigate BatchMaintenance hang").
Natural fit alongside bookmarks (4.5d) — bookmarks are session snapshots,
recipes are reusable templates.

### Auth token on bridge HTTP endpoints
Anyone on localhost can hit `http://localhost:5959/debug/*` and drive the
debugger. Low risk on a personal machine, real risk on a shared box. Phase 5:
generate token at VS startup, write to a user-readable-only file, MCP server
reads it and includes `Authorization: Bearer <token>` on every call.

### Conditional / log-point breakpoints
EnvDTE supports both. Currently only `condition` is exposed. Logpoints
(print without stopping) are very useful for PSGTMS where breaking on a hot
loop is impractical. Add to `set_breakpoint` once basics work.

### Output / Debug window streaming — IMPLEMENTED
Built without WebSocket — reused the same long-poll pattern as
`wait_for_debugger_change` (version counter + `ManualResetEventSlim`),
hooked via `Events2.get_TextEditorEvents(TextDocument)` scoped to each
Output pane. Off by default (`set_output_streaming`) to avoid token cost
on sessions that don't need it. See `BridgeServer.cs` "Output/Debug window
streaming" region and `mcp-server/index.js` (`set_output_streaming`,
`list_output_panes`, `get_output_snapshot`, `wait_for_output`).

**Bug found and fixed same day:** the first version of `OnOutputPaneChanged`
re-read the entire accumulated pane text from `StartPoint` on every single
`LineChanged` event, to diff against a remembered "last full text" and find
the new suffix. That's O(total length) per line, O(n²) overall for n
printed lines — and it ran synchronously on VS's UI thread (COM events fire
there), so a process printing many lines with streaming enabled froze VS
entirely (every bridge call timed out, looking like a hang rather than a
slow response). Fixed by using the `start`/`end` `TextPoint`s the
`LineChanged` event already provides to read only the changed region —
O(delta) per event, not O(total). `OutputPaneBuffer.LastFullText` and the
diff logic were removed entirely since they're no longer needed.

### Symbol / source path resolution helpers — IMPLEMENTED (partially)
`get_loaded_modules` lists loaded modules with `attached` status and
best-effort symbol info. Symbol-status detection uses late-bound
reflection to probe candidate property names (`SymbolStatus`,
`HasSymbolsLoaded`, etc.) since the exact EnvDTE interop surface wasn't
confirmed ahead of time — `symbolsLoaded` may be `null` (unknown) on some
VS versions even when `symbolStatusRaw` has a value. Off by default
(`set_module_diagnostics`). Driving the Symbol Settings dialog
programmatically (adding search paths) was explicitly out of scope for
this pass — diagnostic visibility only, not remediation.

### "Why are we here?" — auto-explanation at breakpoint hit
When a breakpoint hits, Claude (in driver or observer mode) automatically
reads the call stack, locals, and surrounding source, and explains in chat
why the program is here. Reduces the constant "now check the locals"
prompting. Natural fit with observer mode.

---

# Out of scope (locked in)

Documenting these so they don't get re-litigated.

- **Automating the Windows credential dialog for msvsmon.** Security
  boundary. Pre-cache via `cmdkey` or enter manually first time per session.
  The MCP `attach_process` tool surfaces a clear 401 message.

- **Reverse-engineering msvsmon's wire protocol.** Closed/proprietary.
  We use EnvDTE (which sits above it) — VS itself handles the protocol.

- **Replacing VS's debugger with vsdbg/DAP.** Office mandates msvsmon;
  DAP doesn't talk to msvsmon. Considered and rejected in early planning.

- **Claude unilaterally taking or stealing control.** Claude can *request*
  handoff via `request_user_handoff`, but cannot mutate `controlMode`
  itself. The `/control` endpoint that changes mode is callable only from
  inside the VS process, never via MCP. This is the central safety
  invariant of the collaboration model.

- **Auto-resuming from emergency stop.** No timeouts, no "Claude can ask
  to resume." User must consciously re-arm. If this becomes annoying in
  practice, revisit — but defaulting to "stays stopped" is the right
  safety default.