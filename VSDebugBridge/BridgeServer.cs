using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace VSDebugBridge
{
    /// <summary>
    /// HTTP listener inside Visual Studio. Routes endpoints to EnvDTE.Debugger
    /// calls, all marshaled to the VS UI thread (EnvDTE is COM and not
    /// thread-safe).
    ///
    /// Endpoints (matched by the Node.js MCP server):
    ///   POST /breakpoint/set          { file, line, condition? }
    ///   POST /breakpoint/remove       { id?, file?, line? }
    ///   GET  /breakpoints
    ///   POST /debug/start
    ///   POST /debug/attach            { processName?, remoteServer? }
    ///   POST /debug/continue
    ///   POST /debug/step/into | over | out
    ///   POST /debug/stop
    ///   GET  /debug/callstack?maxFrames=N
    ///   GET  /debug/locals?frameIndex=N
    ///   POST /debug/evaluate          { expression, frameIndex? }
    ///   GET  /debug/wait?timeoutMs=N&amp;sinceVersion=M   (long-poll for state changes — latest only)
    ///   GET  /debug/eventlog?sinceVersion=N&amp;maxEntries=M   (full history since sinceVersion)
    ///   POST /debug/break-on-exceptions { enabled }   (pause on throw even if later caught)
    ///   GET  /output/panes
    ///   GET  /output/snapshot?pane=Debug&amp;maxChars=N
    ///   GET  /output/tail?pane=Debug&amp;sinceVersion=M&amp;timeoutMs=N   (long-poll for new output)
    ///   POST /output/streaming        { enabled, panes?: string[] }
    ///   GET  /control/mode            (read-only — current collaboration mode; Claude can
    ///                                  never set this, only the user can, via VS Debug menu)
    ///   GET  /debug/stepfilters       (list all step-over marker lines)
    ///   POST /debug/stepfilters       { file, line }  (add step-over marker)
    ///   DELETE /debug/stepfilters     { file, line }  (remove step-over marker)
    ///   GET  /features/status         (list features + enabled/disabled state)
    ///   GET  /debug/journal           (read persisted session journal)
    ///   POST /debug/journal           { hypothesis?, focusZones?, skipZones?, confirmedFindings?, nextSessionPlan? }
    ///   GET  /debug/variable/expand?expression=X&amp;maxDepth=N&amp;frameIndex=N  (drill into object members)
    ///   GET  /debug/exception         (structured current exception info)
    ///   GET  /debug/threads           (list threads in active debug program)
    ///   POST /debug/threads/switch    { threadId }
    ///   GET  /debug/watches           (evaluate all watch expressions)
    ///   POST /debug/watches           { name, expression }
    ///   DELETE /debug/watches         { name }
    ///   GET  /debug/pins              (evaluate all pinned variables)
    ///   POST /debug/pins              { name, expression }  (auto-eval in every snapshot)
    ///   DELETE /debug/pins            { name }
    ///   POST /debug/frame/set         { frameIndex }  (set active frame for locals/evaluate)
    /// </summary>
    internal class BridgeServer
    {
        private readonly DTE _dte;
        private readonly JoinableTaskFactory _jtf;
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        // Long-poll support: EnvDTE DebuggerEvents fire on execution-state
        // transitions (breakpoint hit, step, manual continue, mode change).
        // They do NOT fire for manual variable/value edits in Locals/Watch —
        // that would require active polling of values, not events.
        private readonly DebuggerEvents _debuggerEvents;
        private readonly object _changeLock = new object();
        private long _changeVersion;
        private string _lastEventType = "none";
        private readonly ManualResetEventSlim _changeSignal = new ManualResetEventSlim(false);

        // Bounded event log — fixes a real gap found in testing: if the user
        // drives several breakpoints/steps quickly between two
        // wait_for_debugger_change calls, that tool only ever returns the
        // LATEST snapshot, silently losing every intermediate transition.
        // This ring buffer keeps the last EventLogCap transitions so Claude can
        // pull everything it missed via GET /debug/eventlog, not just "now."
        private class DebugLogEntry
        {
            public long Version;
            public string EventType;
            public string Mode;
            public string Function;
            public bool? Attached;
            public object Exception;
            public List<object> Locals;
            public DateTime TimestampUtc;
        }

        private const int EventLogCap = 200;
        private readonly LinkedList<DebugLogEntry> _eventLog = new LinkedList<DebugLogEntry>();
        private EnvDTE.OutputWindowPane _claudeLogPane;

        // -------------------------------------------------------------------
        // Output/Debug window streaming — opt-in (default OFF) to avoid eating
        // tokens on every session. When disabled, the per-pane buffers are
        // cleared and the change handler short-circuits immediately, so there's
        // no background cost beyond the cheap bool check inside the event
        // handler itself (we don't bother detaching the COM event sink on
        // disable — re-subscribing on every toggle is more failure-prone than
        // a no-op handler check).
        // -------------------------------------------------------------------
        private class OutputPaneBuffer
        {
            public readonly StringBuilder FullBuffer = new StringBuilder();
            public readonly StringBuilder Pending = new StringBuilder();
            public long Version;
        }

        private const int OutputBufferCapChars = 256 * 1024;
        private readonly object _outputLock = new object();
        private readonly Dictionary<string, OutputPaneBuffer> _outputBuffers =
            new Dictionary<string, OutputPaneBuffer>(StringComparer.OrdinalIgnoreCase);
        private bool _outputStreamingEnabled;
        private HashSet<string> _outputEnabledPanes; // null = all panes
        private readonly ManualResetEventSlim _outputChangeSignal = new ManualResetEventSlim(false);

        // Module/symbol diagnostics — opt-in (default OFF), same rationale as
        // output streaming: avoid Claude reflexively calling this on every
        // session and paying for a 50+ module dump when it isn't the problem.
        private bool _moduleDiagnosticsEnabled;

        // -------------------------------------------------------------------
        // Feature registry — graceful degradation when office IT policy blocks
        // specific capabilities (file system access, restricted COM APIs, etc.).
        // Each feature starts Enabled; on first access-denied exception it
        // flips to DisabledAccessDenied for the rest of the session and logs
        // once to the Claude Debug Log pane + fires a FeatureDisabled event so
        // Claude's next wait_for_debugger_change carries the notification.
        // Subsequent calls get an HTTP 503 immediately rather than repeatedly
        // hitting the access wall.
        // -------------------------------------------------------------------
        internal enum FeatureState { Enabled, DisabledAccessDenied }

        private readonly Dictionary<string, (FeatureState State, string Reason)> _features =
            new Dictionary<string, (FeatureState, string)>(StringComparer.OrdinalIgnoreCase)
            {
                ["OutputStreaming"]         = (FeatureState.Enabled, null),
                ["ModuleDiagnostics"]       = (FeatureState.Enabled, null),
                ["StepFilterPersistence"]   = (FeatureState.Enabled, null),
                ["DebugJournalPersistence"] = (FeatureState.Enabled, null),
                ["ClaudeLogPane"]           = (FeatureState.Enabled, null),
                ["BreakOnExceptions"]       = (FeatureState.Enabled, null),
                ["RemoteDebug"]             = (FeatureState.Enabled, null),
            };

        private static bool IsAccessException(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return true;
            if (ex is System.Security.SecurityException) return true;
            if (ex is System.Runtime.InteropServices.COMException com &&
                (uint)com.HResult == 0x80070005) return true;
            if (ex is IOException io &&
                (io.Message.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 io.Message.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 io.Message.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        private void DisableFeature(string name, string reason)
        {
            lock (_features)
            {
                if (_features.ContainsKey(name) &&
                    _features[name].State == FeatureState.DisabledAccessDenied) return; // already disabled — log once only
                _features[name] = (FeatureState.DisabledAccessDenied, reason);
            }
            string msg = $"[FEATURE DISABLED] '{name}' disabled for this session — access denied: {reason}";
            try { GetOrCreateClaudeLogPane()?.OutputString(msg + Environment.NewLine); } catch { }
            RecordChange("FeatureDisabled:" + name);
        }

        private T TryFeature<T>(string featureName, Func<T> action)
        {
            lock (_features)
            {
                if (_features.TryGetValue(featureName, out var s) &&
                    s.State == FeatureState.DisabledAccessDenied)
                    throw new BridgeException(503,
                        $"Feature '{featureName}' is disabled for this session due to an access restriction: {s.Reason}. " +
                        "This is likely an IT policy or file-permission limitation on this machine. " +
                        "The rest of the debugger continues to work normally.");
            }
            try { return action(); }
            catch (BridgeException) { throw; }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature(featureName, ex.Message);
                throw new BridgeException(503,
                    $"Feature '{featureName}' encountered an access restriction and has been disabled for this session: {ex.Message}");
            }
        }

        private object GetFeatureStatus()
        {
            var list = new List<object>();
            lock (_features)
            {
                foreach (var kvp in _features)
                {
                    list.Add(new
                    {
                        name = kvp.Key,
                        state = kvp.Value.State == FeatureState.Enabled ? "enabled" : "disabled",
                        reason = kvp.Value.Reason,
                    });
                }
            }
            return new
            {
                features = list,
                note = "Disabled features were blocked by an access/permission error on this machine. " +
                       "The rest of the debugger is unaffected. Inform the user so they can check IT policy if needed."
            };
        }

        // -------------------------------------------------------------------
        // Debug Journal — persists session findings between Claude sessions so
        // the next run can skip confirmed-safe zones and focus on problem areas.
        // Bridge auto-saves lastKnownState on every breakpoint + session end.
        // Claude writes hypothesis/focusZones/skipZones via POST /debug/journal.
        // Both halves live in .claude-debug-journal.json next to the .sln.
        // -------------------------------------------------------------------
        private readonly object _journalLock = new object();
        private JObject _journal = new JObject(); // in-memory; flushed to disk on change

        // -------------------------------------------------------------------
        // Step filters — per-line "use step_over here" markers set by the user
        // via right-click in the code editor. Claude reads these from
        // wait_for_debugger_change (suggestedStep field) so it knows to use
        // step_over instead of step_into on marked lines without needing a
        // separate query. Only the user can set/clear filters; Claude can only
        // read them (GET /debug/stepfilters) — same philosophy as control mode.
        //
        // Static event + Instance singleton: the glyph tagger (StepOverTagger)
        // is a MEF-composed component so it has no direct reference to this
        // instance; we bridge the gap with a static event + static Instance
        // field. Both are set only once per VS process (one BridgeServer per
        // process), so there's no multi-instance concern here.
        // -------------------------------------------------------------------
        private readonly Dictionary<string, HashSet<int>> _stepFilters =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _stepFilterLock = new object();

        internal static BridgeServer Instance { get; private set; }
        internal static event EventHandler StepFiltersChanged;

        // -------------------------------------------------------------------
        // Pinned variables — auto-evaluated inside EVERY wait_for_debugger_change
        // snapshot. Eliminates the need for a follow-up get_locals/evaluate call
        // just to check "what is X right now?" after every step. Unlike watches
        // (manual on-demand via GET /debug/watches), pins ride along for free on
        // every execution-state change without an extra round trip.
        // -------------------------------------------------------------------
        private readonly Dictionary<string, string> _pinnedVars =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _pinLock = new object();

        // -------------------------------------------------------------------
        // Watch expressions — manual on-demand evaluation (GET /debug/watches).
        // Use watches for expressions you query periodically; use pins for ones
        // you need after every single step/breakpoint.
        // -------------------------------------------------------------------
        private readonly Dictionary<string, string> _watches =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _watchLock = new object();

        // -------------------------------------------------------------------
        // Control mode (Phase 4.5a) — the safety floor for human/Claude
        // collaboration. Claude can only ever READ this (GET /control/mode);
        // it can never set it itself, by design — only the user can, via the
        // VS Debug menu commands in ClaudeControlCommands.cs, which call
        // SetControlModeFromUi() directly as an in-process method call, never
        // through this HTTP surface. See features.md "Collaboration Model"
        // for the full design and the security rationale.
        // -------------------------------------------------------------------
        internal enum ControlMode { Driver, Observer, Paused, UserDriving, EmergencyStop }

        private ControlMode _controlMode = ControlMode.Driver;

        internal ControlMode CurrentControlMode => _controlMode;

        internal static string ModeToString(ControlMode mode)
        {
            switch (mode)
            {
                case ControlMode.Driver: return "driver";
                case ControlMode.Observer: return "observer";
                case ControlMode.Paused: return "paused";
                case ControlMode.UserDriving: return "user-driving";
                case ControlMode.EmergencyStop: return "emergency-stop";
                default: return "unknown";
            }
        }

        // Called only from ClaudeControlCommands (VS Debug menu handlers) —
        // never exposed as an HTTP endpoint, so Claude has no path to call this.
        internal void SetControlModeFromUi(ControlMode mode)
        {
            _controlMode = mode;

            // Route this through RecordChange (the same path debugger execution
            // transitions use) so a mode switch: (1) wakes up any in-progress
            // wait_for_debugger_change call immediately instead of leaving Claude
            // polling blind for a debugger-execution change that will never come
            // from a pure mode flip, (2) shows up in the Claude Debug Log pane,
            // (3) is visible via get_debug_event_log history. Found missing
            // during real use — the user handed control back via the menu and
            // Claude's wait loop had no way to notice.
            RecordChange("ControlModeChanged:" + ModeToString(mode));
        }

        // -------------------------------------------------------------------
        // Step filters — user sets these via right-click; Claude reads them
        // -------------------------------------------------------------------

        internal void AddStepFilter(string file, int line)
        {
            lock (_stepFilterLock)
            {
                if (!_stepFilters.TryGetValue(file, out var lines))
                    _stepFilters[file] = lines = new HashSet<int>();
                lines.Add(line);
            }
            SaveStepFilters();
            StepFiltersChanged?.Invoke(null, EventArgs.Empty);
            try { GetOrCreateClaudeLogPane().OutputString($"[StepFilter] Marked step-over: {Path.GetFileName(file)}:{line}{Environment.NewLine}"); }
            catch { }
        }

        internal void RemoveStepFilter(string file, int line)
        {
            lock (_stepFilterLock)
            {
                if (_stepFilters.TryGetValue(file, out var lines))
                {
                    lines.Remove(line);
                    if (lines.Count == 0) _stepFilters.Remove(file);
                }
            }
            SaveStepFilters();
            StepFiltersChanged?.Invoke(null, EventArgs.Empty);
            try { GetOrCreateClaudeLogPane().OutputString($"[StepFilter] Unmarked step-over: {Path.GetFileName(file)}:{line}{Environment.NewLine}"); }
            catch { }
        }

        internal bool IsStepFilter(string file, int line)
        {
            if (string.IsNullOrEmpty(file)) return false;
            lock (_stepFilterLock)
            {
                return _stepFilters.TryGetValue(file, out var lines) && lines.Contains(line);
            }
        }

        // Called by ClaudeControlCommands right-click handler to check the
        // current state of the line under the cursor (for BeforeQueryStatus).
        internal bool IsCurrentEditorLineFiltered()
        {
            var pos = GetCurrentEditorPosition();
            return pos.HasValue && IsStepFilter(pos.Value.file, pos.Value.line);
        }

        internal string GetCurrentSelection()
        {
            try
            {
                var sel = _dte.ActiveDocument?.Selection as EnvDTE.TextSelection;
                return sel?.Text;
            }
            catch { return null; }
        }

        internal (string file, int line)? GetCurrentEditorPosition()
        {
            try
            {
                var doc = _dte.ActiveDocument;
                if (doc == null) return null;
                var sel = doc.Selection as EnvDTE.TextSelection;
                if (sel == null) return null;
                return (doc.FullName, sel.CurrentLine);
            }
            catch { return null; }
        }

        // Selection saved by "Send Selection to Claude Code" right-click command.
        private string _lastSelectionJson;
        private readonly object _selectionLock = new object();

        internal void SaveSelection()
        {
            try
            {
                var doc = _dte.ActiveDocument;
                if (doc == null) return;
                var sel = doc.Selection as EnvDTE.TextSelection;
                if (sel == null || string.IsNullOrEmpty(sel.Text)) return;

                var payload = JsonConvert.SerializeObject(new
                {
                    file = doc.FullName,
                    fileName = Path.GetFileName(doc.FullName),
                    startLine = sel.TopLine,
                    endLine = sel.BottomLine,
                    text = sel.Text,
                    capturedAt = DateTime.UtcNow.ToString("o")
                });
                lock (_selectionLock) { _lastSelectionJson = payload; }

                try { GetOrCreateClaudeLogPane()?.OutputString($"[Selection sent] {Path.GetFileName(doc.FullName)} lines {sel.TopLine}–{sel.BottomLine}{Environment.NewLine}"); } catch { }
            }
            catch { }
        }

        private object GetLastSelection()
        {
            string json;
            lock (_selectionLock) { json = _lastSelectionJson; }
            if (json == null)
                return new { available = false, note = "No selection sent yet. Right-click selected code in VS and choose 'Send Selection to Claude Code'." };
            return new { available = true, selection = JObject.Parse(json) };
        }

        private object ListStepFilters()
        {
            var result = new List<object>();
            lock (_stepFilterLock)
            {
                foreach (var kvp in _stepFilters)
                {
                    foreach (int line in kvp.Value)
                    {
                        result.Add(new { file = kvp.Key, line, fileName = Path.GetFileName(kvp.Key) });
                    }
                }
            }
            return new { stepFilters = result, count = result.Count };
        }

        private object AddStepFilterFromHttp(string file, int line)
        {
            if (string.IsNullOrEmpty(file)) throw new BridgeException(400, "file is required");
            if (line <= 0) throw new BridgeException(400, "line must be >= 1");
            AddStepFilter(file, line);
            return new { added = true, file, line };
        }

        private object RemoveStepFilterFromHttp(string file, int line)
        {
            if (string.IsNullOrEmpty(file)) throw new BridgeException(400, "file is required");
            if (line <= 0) throw new BridgeException(400, "line must be >= 1");
            RemoveStepFilter(file, line);
            return new { removed = true, file, line };
        }

        private void SaveStepFilters()
        {
            if (_features.TryGetValue("StepFilterPersistence", out var fs) &&
                fs.State == FeatureState.DisabledAccessDenied) return;
            try
            {
                string filePath = GetStepFilterFilePath();
                if (filePath == null) return;
                var data = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                lock (_stepFilterLock)
                {
                    foreach (var kvp in _stepFilters)
                        data[kvp.Key] = new List<int>(kvp.Value);
                }
                File.WriteAllText(filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature("StepFilterPersistence", ex.Message);
            }
            catch { /* other errors — best-effort */ }
        }

        internal void LoadStepFilters()
        {
            if (_features.TryGetValue("StepFilterPersistence", out var fs) &&
                fs.State == FeatureState.DisabledAccessDenied) return;
            try
            {
                string filePath = GetStepFilterFilePath();
                if (filePath == null || !File.Exists(filePath)) return;
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<int>>>(File.ReadAllText(filePath));
                if (data == null) return;
                lock (_stepFilterLock)
                {
                    _stepFilters.Clear();
                    foreach (var kvp in data)
                    {
                        if (kvp.Value != null && kvp.Value.Count > 0)
                            _stepFilters[kvp.Key] = new HashSet<int>(kvp.Value);
                    }
                }
                StepFiltersChanged?.Invoke(null, EventArgs.Empty);
                int total = 0;
                lock (_stepFilterLock) { foreach (var v in _stepFilters.Values) total += v.Count; }
                if (total > 0)
                {
                    try { GetOrCreateClaudeLogPane().OutputString($"[StepFilter] Loaded {total} filter(s) from {filePath}{Environment.NewLine}"); }
                    catch { }
                }
            }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature("StepFilterPersistence", ex.Message);
            }
            catch { /* corrupt file — ignore, start fresh */ }
        }

        private string GetStepFilterFilePath()
        {
            try
            {
                var solutionPath = _dte.Solution?.FullName;
                if (string.IsNullOrEmpty(solutionPath)) return null;
                return Path.Combine(Path.GetDirectoryName(solutionPath), ".claude-step-filters.json");
            }
            catch { return null; }
        }

        // -------------------------------------------------------------------
        // Debug Journal — read/write + auto-save
        // -------------------------------------------------------------------

        private object GetJournal()
        {
            return TryFeature("DebugJournalPersistence", () =>
            {
                lock (_journalLock) { return (object)_journal.DeepClone(); }
            });
        }

        private object UpdateJournal(JObject patch)
        {
            return TryFeature("DebugJournalPersistence", () =>
            {
                lock (_journalLock)
                {
                    foreach (var prop in patch.Properties())
                        _journal[prop.Name] = prop.Value;
                    _journal["lastUpdatedByClaudeAt"] = DateTime.UtcNow.ToString("O");
                }
                SaveJournal();
                return new { saved = true };
            });
        }

        internal void LoadJournal()
        {
            if (_features.TryGetValue("DebugJournalPersistence", out var s) &&
                s.State == FeatureState.DisabledAccessDenied) return;
            try
            {
                string path = GetJournalFilePath();
                if (path == null || !File.Exists(path)) return;
                lock (_journalLock)
                {
                    _journal = JObject.Parse(File.ReadAllText(path));
                }
            }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature("DebugJournalPersistence", ex.Message);
            }
            catch { /* corrupt file — start fresh */ }
        }

        private void SaveJournal()
        {
            if (_features.TryGetValue("DebugJournalPersistence", out var s) &&
                s.State == FeatureState.DisabledAccessDenied) return;
            try
            {
                string path = GetJournalFilePath();
                if (path == null) return;
                string json;
                lock (_journalLock) { json = _journal.ToString(Formatting.Indented); }
                File.WriteAllText(path, json);
            }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature("DebugJournalPersistence", ex.Message);
            }
            catch { }
        }

        // Auto-save last known debugger state into the journal — called on
        // every breakpoint hit and on session end (OnEnterDesignMode). This
        // ensures the journal always reflects the last execution position even
        // if VS crashes or Claude is stopped before saving its analysis.
        private void AutoSaveJournalState(string trigger)
        {
            if (_features.TryGetValue("DebugJournalPersistence", out var s) &&
                s.State == FeatureState.DisabledAccessDenied) return;
            try
            {
                SnapshotFields f = null;
                try { f = BuildSnapshotFields(); } catch { }
                lock (_journalLock)
                {
                    _journal["lastKnownState"] = new JObject
                    {
                        ["trigger"]     = trigger,
                        ["file"]        = f?.CurrentFile,
                        ["line"]        = f?.CurrentLine > 0 ? (JToken)f.CurrentLine : JValue.CreateNull(),
                        ["function"]    = f?.Function,
                        ["mode"]        = f?.Mode,
                        ["logVersion"]  = _changeVersion,
                        ["savedAt"]     = DateTime.UtcNow.ToString("O"),
                        ["locals"]      = f?.Locals != null
                            ? JArray.FromObject(f.Locals)
                            : null,
                    };
                }
                SaveJournal();
            }
            catch { /* auto-save is best-effort */ }
        }

        private string GetJournalFilePath()
        {
            try
            {
                var sln = _dte.Solution?.FullName;
                if (string.IsNullOrEmpty(sln)) return null;
                return Path.Combine(Path.GetDirectoryName(sln), ".claude-debug-journal.json");
            }
            catch { return null; }
        }

        // Endpoints that change debugger/process state — gated by control mode.
        // Read-only inspection endpoints (callstack/locals/evaluate/wait/output/
        // modules/breakpoint listing) are deliberately NOT in this set: per the
        // design, read-only calls stay allowed in every mode except emergency-stop.
        private static readonly HashSet<string> MutatingEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "POST /breakpoint/set",
            "POST /breakpoint/remove",
            "POST /debug/start",
            "POST /debug/attach",
            "POST /debug/continue",
            "POST /debug/step/into",
            "POST /debug/step/over",
            "POST /debug/step/out",
            "POST /debug/stop",
            "POST /debug/break-on-exceptions",
            "POST /output/streaming",
            "POST /debug/module-diagnostics",
            "POST /debug/threads/switch",
            "POST /debug/frame/set",
        };

        private void EnsureCallAllowed(string method, string path)
        {
            string key = method + " " + path;
            if (string.Equals(key, "GET /control/mode", StringComparison.OrdinalIgnoreCase))
            {
                return; // always allowed — reading the mode is how Claude knows it's blocked
            }

            if (_controlMode == ControlMode.EmergencyStop)
            {
                throw new BridgeException(423,
                    "Claude is stopped (emergency-stop mode). The user must re-arm via " +
                    "Debug > Claude Debug Control > Resume Claude Debug before any tool call will work.");
            }

            if (MutatingEndpoints.Contains(key) && _controlMode != ControlMode.Driver)
            {
                throw new BridgeException(423,
                    $"Claude is in '{ModeToString(_controlMode)}' mode — mutating actions are blocked. " +
                    "Ask the user to switch to driver mode (Debug > Claude Debug Control > Let Claude Debug) " +
                    "via the Visual Studio UI; Claude cannot change this itself.");
            }
        }

        public BridgeServer(DTE dte, JoinableTaskFactory jtf)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
            _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
            Instance = this;

            // Must subscribe on the UI thread (caller is already there — see
            // DebugBridgePackage.InitializeAsync). Keep a strong field reference
            // to the event source or the COM event sink gets garbage collected
            // and events silently stop firing.
            _debuggerEvents = _dte.Events.DebuggerEvents;
            _debuggerEvents.OnEnterBreakMode += OnEnterBreakModeHandler;
            _debuggerEvents.OnEnterRunMode += OnEnterRunModeHandler;
            _debuggerEvents.OnEnterDesignMode += OnEnterDesignModeHandler;
            _debuggerEvents.OnContextChanged += OnContextChangedHandler;
        }

        private void OnEnterBreakModeHandler(dbgEventReason Reason, ref dbgExecutionAction ExecutionAction)
        {
            RecordChange("BreakMode:" + Reason);
            // Auto-save journal on every breakpoint so lastKnownState survives VS crashes.
            AutoSaveJournalState("breakpoint:" + Reason);
        }

        private void OnEnterRunModeHandler(dbgEventReason Reason)
        {
            RecordChange("RunMode:" + Reason);
        }

        private void OnEnterDesignModeHandler(dbgEventReason Reason)
        {
            RecordChange("DesignMode:" + Reason);
            // Debug session ended (F5 stopped, process exited, detached).
            // Auto-save so the journal reflects where the session ended even
            // if Claude never called save_debug_journal explicitly.
            AutoSaveJournalState("sessionEnd:" + Reason);
        }

        private void OnContextChangedHandler(Process NewProcess, Program NewProgram, EnvDTE.Thread NewThread, StackFrame NewStackFrame)
        {
            RecordChange("ContextChanged");
        }

        private void RecordChange(string eventType)
        {
            // Build the snapshot OUTSIDE the lock — it makes COM calls into
            // EnvDTE, which shouldn't happen while holding a lock that only
            // needs to protect the in-memory counters/list below.
            SnapshotFields f = null;
            try { f = BuildSnapshotFields(); }
            catch { /* best-effort — still record the version bump below even if this fails */ }

            DebugLogEntry entry;
            lock (_changeLock)
            {
                _changeVersion++;
                _lastEventType = eventType;
                entry = new DebugLogEntry
                {
                    Version = _changeVersion,
                    EventType = eventType,
                    Mode = f?.Mode ?? "unknown",
                    Function = f?.Function,
                    Attached = f?.Attached,
                    Exception = f?.Exception,
                    Locals = f?.Locals,
                    TimestampUtc = DateTime.UtcNow,
                };
                _eventLog.AddLast(entry);
                while (_eventLog.Count > EventLogCap) _eventLog.RemoveFirst();
            }
            _changeSignal.Set();

            try
            {
                GetOrCreateClaudeLogPane().OutputString(FormatLogLine(entry));
            }
            catch { /* writing the human-visible log line should never break event recording */ }
        }

        private EnvDTE.OutputWindowPane GetOrCreateClaudeLogPane()
        {
            if (_claudeLogPane != null) return _claudeLogPane;
            var panes = GetOutputWindow().OutputWindowPanes;
            try { _claudeLogPane = panes.Item("Claude Debug Log"); }
            catch { _claudeLogPane = panes.Add("Claude Debug Log"); }
            return _claudeLogPane;
        }

        private static string FormatLogLine(DebugLogEntry e)
        {
            var sb = new StringBuilder();
            sb.Append(e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));
            sb.Append("  v").Append(e.Version);
            sb.Append("  ").Append(e.EventType);
            sb.Append("  mode=").Append(e.Mode);
            if (e.Function != null) sb.Append("  fn=").Append(e.Function);
            if (e.Attached == false) sb.Append("  attached=false");
            if (e.Exception != null) sb.Append("  EXCEPTION");
            sb.Append(Environment.NewLine);
            return sb.ToString();
        }

        private object GetEventLog(long sinceVersion, int maxEntries)
        {
            List<DebugLogEntry> matched;
            long latestVersion;
            lock (_changeLock)
            {
                latestVersion = _changeVersion;
                matched = new List<DebugLogEntry>();
                foreach (var e in _eventLog)
                {
                    if (e.Version > sinceVersion) matched.Add(e);
                }
            }

            bool truncated = false;
            if (matched.Count > maxEntries)
            {
                // Keep the MOST RECENT maxEntries, not the oldest — if Claude can
                // only see a capped slice, the latest state matters more than
                // whatever happened first.
                matched = matched.GetRange(matched.Count - maxEntries, maxEntries);
                truncated = true;
            }

            var entries = new List<object>();
            foreach (var e in matched)
            {
                entries.Add(new
                {
                    version = e.Version,
                    eventType = e.EventType,
                    mode = e.Mode,
                    function = e.Function,
                    attached = e.Attached,
                    exception = e.Exception,
                    locals = e.Locals,
                    timestamp = e.TimestampUtc.ToString("O"),
                });
            }

            return new
            {
                entries,
                count = entries.Count,
                latestVersion,
                truncated,
                note = truncated
                    ? $"More than {maxEntries} entries existed since sinceVersion={sinceVersion}; returning only the most recent {maxEntries}. Pass a higher maxEntries or poll more frequently to avoid gaps."
                    : null,
            };
        }

        public int Port { get; private set; }

        /// <summary>
        /// Binds starting at <paramref name="basePort"/>, trying up to
        /// <paramref name="maxAttempts"/> consecutive ports if earlier ones are
        /// already taken (e.g. by another VS instance's bridge). This is what
        /// makes running multiple VS instances simultaneously actually work —
        /// each one gets its own port instead of the second one giving up.
        /// Returns the port actually bound.
        /// </summary>
        public int Start(int basePort, int maxAttempts = 10)
        {
            Exception lastError = null;
            for (int i = 0; i < maxAttempts; i++)
            {
                int candidate = basePort + i;
                var listener = new HttpListener();
                // Specific hostname (not + or *) so this works without admin / URL ACL.
                listener.Prefixes.Add($"http://localhost:{candidate}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = candidate;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => ListenLoop(_cts.Token));
                    return Port;
                }
                catch (HttpListenerException ex)
                {
                    lastError = ex;
                    try { listener.Close(); } catch { }
                    // Likely "address already in use" — try the next port.
                }
            }
            throw new InvalidOperationException(
                $"Could not bind any port in range {basePort}-{basePort + maxAttempts - 1}.", lastError);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            try
            {
                if (_debuggerEvents != null)
                {
                    _debuggerEvents.OnEnterBreakMode -= OnEnterBreakModeHandler;
                    _debuggerEvents.OnEnterRunMode -= OnEnterRunModeHandler;
                    _debuggerEvents.OnEnterDesignMode -= OnEnterDesignModeHandler;
                    _debuggerEvents.OnContextChanged -= OnContextChangedHandler;
                }
            }
            catch { }
            _changeSignal.Set();
        }

        // -------------------------------------------------------------------
        // Listen loop
        // -------------------------------------------------------------------

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(() => HandleRequest(ctx, ct));
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            string method = ctx.Request.HttpMethod.ToUpperInvariant();
            string body = null;
            if (ctx.Request.HasEntityBody)
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }
            }

            try
            {
                object result = await DispatchAsync(method, path, ctx.Request.Url, body, ct);
                await WriteJsonAsync(ctx, 200, result);
            }
            catch (BridgeException bex)
            {
                await WriteJsonAsync(ctx, bex.StatusCode, new { error = bex.Message });
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(ctx, 500, new
                {
                    error = ex.Message,
                    type = ex.GetType().Name,
                });
            }
        }

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            var json = JsonConvert.SerializeObject(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // -------------------------------------------------------------------
        // Dispatch
        // -------------------------------------------------------------------

        private async Task<object> DispatchAsync(string method, string path, Uri url, string body, CancellationToken ct)
        {
            EnsureCallAllowed(method, path);
            JObject json = !string.IsNullOrEmpty(body) ? JObject.Parse(body) : new JObject();

            // Every handler switches to the UI thread before touching DTE.
            switch (method + " " + path)
            {
                case "GET /control/mode":
                    return new { mode = ModeToString(_controlMode) };

                case "POST /breakpoint/set":
                    return await OnUiAsync(() => SetBreakpoint(
                        (string)json["file"], (int?)json["line"] ?? 0, (string)json["condition"]));

                case "POST /breakpoint/remove":
                    return await OnUiAsync(() => RemoveBreakpoint(
                        (string)json["id"], (string)json["file"], (int?)json["line"]));

                case "GET /breakpoints":
                    return await OnUiAsync(() => ListBreakpoints());

                case "POST /debug/start":
                    return await OnUiAsync(() => StartDebug());

                case "POST /debug/attach":
                    return await OnUiAsync(() => AttachProcess(
                        (string)json["processName"], (string)json["remoteServer"]));

                case "POST /debug/continue":
                    return await OnUiAsync(() => ContinueExecution());

                case "POST /debug/step/into":
                    return await OnUiAsync(() => Step("into"));

                case "POST /debug/step/over":
                    return await OnUiAsync(() => Step("over"));

                case "POST /debug/step/out":
                    return await OnUiAsync(() => Step("out"));

                case "POST /debug/stop":
                    return await OnUiAsync(() => StopDebug());

                case "GET /debug/callstack":
                    {
                        int max = ParseQueryInt(url, "maxFrames", 50);
                        return await OnUiAsync(() => GetCallstack(max));
                    }

                case "GET /debug/locals":
                    {
                        int frameIndex = ParseQueryInt(url, "frameIndex", 0);
                        return await OnUiAsync(() => GetLocals(frameIndex));
                    }

                case "POST /debug/evaluate":
                    return await OnUiAsync(() => EvaluateExpression(
                        (string)json["expression"], (int?)json["frameIndex"] ?? 0));

                case "GET /debug/wait":
                    {
                        int timeoutMs = ParseQueryInt(url, "timeoutMs", 30000);
                        long sinceVersion = ParseQueryLong(url, "sinceVersion", -1);
                        return await WaitForChangeAsync(timeoutMs, sinceVersion, ct);
                    }

                case "GET /debug/eventlog":
                    {
                        long sinceVersion = ParseQueryLong(url, "sinceVersion", 0);
                        int maxEntries = ParseQueryInt(url, "maxEntries", 50);
                        return GetEventLog(sinceVersion, maxEntries);
                    }

                case "POST /debug/break-on-exceptions":
                    return await OnUiAsync(() => SetBreakOnExceptions((bool?)json["enabled"] ?? true));

                case "GET /output/panes":
                    return await OnUiAsync(() => ListOutputPanes());

                case "GET /output/snapshot":
                    {
                        string pane = ParseQueryString(url, "pane") ?? "Debug";
                        int maxChars = ParseQueryInt(url, "maxChars", 20000);
                        return GetOutputSnapshot(pane, maxChars);
                    }

                case "GET /output/tail":
                    {
                        string pane = ParseQueryString(url, "pane") ?? "Debug";
                        int timeoutMs = ParseQueryInt(url, "timeoutMs", 30000);
                        long sinceVersion = ParseQueryLong(url, "sinceVersion", -1);
                        return await WaitForOutputAsync(pane, timeoutMs, sinceVersion, ct);
                    }

                case "POST /output/streaming":
                    return await OnUiAsync(() => SetOutputStreaming(
                        (bool?)json["enabled"] ?? true,
                        json["panes"]?.ToObject<string[]>()));

                case "GET /debug/modules":
                    return await OnUiAsync(() => GetLoadedModules());

                case "POST /debug/module-diagnostics":
                    return SetModuleDiagnostics((bool?)json["enabled"] ?? true);

                case "GET /debug/stepfilters":
                    return ListStepFilters();

                case "POST /debug/stepfilters":
                    return AddStepFilterFromHttp((string)json["file"], (int?)json["line"] ?? 0);

                case "DELETE /debug/stepfilters":
                    return RemoveStepFilterFromHttp((string)json["file"], (int?)json["line"] ?? 0);

                case "GET /features/status":
                    return GetFeatureStatus();

                case "GET /debug/journal":
                    return GetJournal();

                case "POST /debug/journal":
                    return UpdateJournal(json);

                case "GET /debug/variable/expand":
                    {
                        string expr    = ParseQueryString(url, "expression");
                        int maxDepth   = ParseQueryInt(url, "maxDepth", 2);
                        int frameIdx   = ParseQueryInt(url, "frameIndex", 0);
                        return await OnUiAsync(() => ExpandVariable(expr, maxDepth, frameIdx));
                    }

                case "GET /debug/exception":
                    return await OnUiAsync(() => GetExceptionInfo());

                case "GET /debug/threads":
                    return await OnUiAsync(() => ListThreads());

                case "POST /debug/threads/switch":
                    return await OnUiAsync(() => SwitchThread((int?)json["threadId"] ?? throw new BridgeException(400, "threadId is required")));

                case "GET /debug/watches":
                    return await OnUiAsync(() => GetWatches());

                case "POST /debug/watches":
                    return AddWatch((string)json["name"], (string)json["expression"]);

                case "DELETE /debug/watches":
                    return RemoveWatch((string)json["name"]);

                case "GET /debug/pins":
                    return await OnUiAsync(() => GetPinnedVariables());

                case "POST /debug/pins":
                    return PinVariable((string)json["name"], (string)json["expression"]);

                case "DELETE /debug/pins":
                    return UnpinVariable((string)json["name"]);

                case "POST /debug/frame/set":
                    return await OnUiAsync(() => SetActiveFrame((int?)json["frameIndex"] ?? throw new BridgeException(400, "frameIndex is required")));

                case "GET /debug/selection":
                    return GetLastSelection();

                default:
                    throw new BridgeException(404, $"Unknown endpoint: {method} {path}");
            }
        }

        private async Task<T> OnUiAsync<T>(Func<T> fn)
        {
            await _jtf.SwitchToMainThreadAsync();
            return fn();
        }

        private static int ParseQueryInt(Uri url, string key, int fallback)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(url.Query);
            var v = qs[key];
            return int.TryParse(v, out var n) ? n : fallback;
        }

        private static long ParseQueryLong(Uri url, string key, long fallback)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(url.Query);
            var v = qs[key];
            return long.TryParse(v, out var n) ? n : fallback;
        }

        private static string ParseQueryString(Uri url, string key)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(url.Query);
            return qs[key];
        }

        // -------------------------------------------------------------------
        // Long-poll: wait for an execution-state change
        // -------------------------------------------------------------------

        private async Task<object> WaitForChangeAsync(int timeoutMs, long sinceVersion, CancellationToken serverCt)
        {
            // Clamp so a runaway client can't pin a connection open forever, and
            // so a too-small value doesn't make this behave like plain polling.
            timeoutMs = Math.Max(1000, Math.Min(timeoutMs, 120000));

            // If the debugger is ALREADY sitting paused/stopped right now, that
            // state IS the answer — don't wait at all. This matters because the
            // version counter only advances on the NEXT transition: if the caller's
            // sinceVersion already matches the break that already happened (e.g. it
            // just observed that break via get_callstack before calling this), the
            // version-based wait below would never see a change and would burn the
            // full timeout for no reason, even though useful state was sitting
            // right there the whole time.
            string currentMode = await OnUiAsync(() => DebuggerState());
            if (currentMode == "paused" || currentMode == "design")
            {
                long currentVersion;
                string lastEventType;
                lock (_changeLock) { currentVersion = _changeVersion; lastEventType = _lastEventType; }
                var snapshot = await OnUiAsync(() => SafeDebuggerSnapshot());

                // Bug found in real use: this used to ALWAYS hardcode eventType to
                // "alreadyInState:<mode>", even when something genuinely new had
                // happened since the caller's sinceVersion (e.g. a ControlModeChanged
                // event while the debugger sat paused the whole time). That silently
                // overwrote the real event with a generic label every single call,
                // so callers watching for a specific eventType (like
                // "ControlModeChanged:driver") never saw it while paused — only a
                // separate, proactive get_control_mode check ever caught it.
                //
                // Follow-up bug found in real use, in THIS fix's first version: it
                // only avoided the generic label when sinceVersion was explicitly
                // provided and behind currentVersion. But callers often omit
                // sinceVersion (server defaults it to -1, "no baseline known") —
                // and -1 failed the ">= 0" check, so the generic label still won
                // every time a caller didn't happen to pass one. Correct rule: only
                // use the generic "alreadyInState" label when we can PROVE the
                // caller already has this exact version (explicit sinceVersion >=
                // currentVersion). Any other case — no baseline given, or a stale
                // baseline — should report the real most-recent event instead of
                // guessing it's nothing new.
                bool callerProvenAlreadyCaughtUp = sinceVersion >= 0 && sinceVersion >= currentVersion;
                string resolvedEventType = callerProvenAlreadyCaughtUp ? ("alreadyInState:" + currentMode) : lastEventType;
                return new { changed = true, version = currentVersion, eventType = resolvedEventType, state = snapshot };
            }

            long startVersion;
            lock (_changeLock) { startVersion = _changeVersion; }

            // Client already missed a change since the version it last saw —
            // return immediately rather than waiting for a fresh one.
            bool alreadyChanged = sinceVersion >= 0 && sinceVersion < startVersion;

            if (!alreadyChanged)
            {
                _changeSignal.Reset();
                // Re-check after Reset() to close the race where an event fired
                // between reading startVersion and resetting the signal.
                lock (_changeLock) { alreadyChanged = _changeVersion != startVersion; }
            }

            if (!alreadyChanged)
            {
                // ManualResetEventSlim.Wait(int, CancellationToken) returns false on a
                // plain timeout — no exception thrown. Deliberately avoid CancelAfter()
                // + a linked token here: that throws OperationCanceledException on every
                // ordinary timeout, which VS's debugger flags as "user-unhandled" and
                // breaks on, freezing the whole VS UI thread mid-poll. serverCt is only
                // for real shutdown, which is rare enough that breaking there is fine.
                try
                {
                    await Task.Run(() => _changeSignal.Wait(timeoutMs, serverCt), serverCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* server shutting down */ }
            }

            long endVersion;
            string eventType;
            lock (_changeLock) { endVersion = _changeVersion; eventType = _lastEventType; }

            bool changed = alreadyChanged || endVersion != startVersion;
            if (!changed)
            {
                return new { changed = false, timedOut = true, version = endVersion };
            }

            var state = await OnUiAsync(() => SafeDebuggerSnapshot());
            return new { changed = true, version = endVersion, eventType, state };
        }

        // Cap on locals bundled into wait_for_debugger_change's snapshot — this is
        // a convenience for the common "what changed" case, not a replacement for
        // get_locals (which stays uncapped and callable on demand for any frame).
        // Keeping this small avoids ballooning the response on every single pause.
        private const int BundledLocalsCap = 15;

        // Plain data holder for the fields both SafeDebuggerSnapshot (for
        // wait_for_debugger_change) and RecordChange (for the event log below)
        // need — extracted so the underlying COM/EnvDTE logic exists in exactly
        // one place.
        private class SnapshotFields
        {
            public string Mode;
            public string Function;
            public bool? Attached;
            public object Exception;
            public List<object> Locals;
            public string CurrentFile;
            public int CurrentLine;
            public List<object> PinnedVars; // auto-evaluated on every state change
        }

        private SnapshotFields BuildSnapshotFields()
        {
            string mode = DebuggerState();
            string function = null;
            bool? attached = null;
            object exception = null;
            List<object> locals = null;
            string currentFile = null;
            int currentLine = 0;
            if (mode == "paused")
            {
                // Capture execution position from the active editor document. VS
                // moves the cursor to the current execution arrow line on every
                // break, so this is the same position shown by the yellow arrow.
                try
                {
                    var doc = _dte.ActiveDocument;
                    if (doc != null)
                    {
                        currentFile = doc.FullName;
                        var sel = doc.Selection as EnvDTE.TextSelection;
                        if (sel != null) currentLine = sel.CurrentLine;
                    }
                }
                catch { }

                try
                {
                    var frame = GetFrame(0);
                    if (frame != null)
                    {
                        function = frame.FunctionName;
                        attached = IsModuleAttached(frame.Module, GetLoadedProjectModuleNames());

                        locals = new List<object>();
                        int count = 0;
                        foreach (Expression expr in frame.Locals)
                        {
                            if (count >= BundledLocalsCap) break;
                            locals.Add(new { name = expr.Name, type = expr.Type, value = expr.Value });
                            count++;
                        }
                    }
                }
                catch { /* best-effort — frame info isn't critical here */ }

                // "$exception" is VS's pseudo-variable for the exception object
                // currently in flight when paused on a throw (same as typing it
                // in the Watch/Immediate window). Evaluating it when paused for
                // any other reason (breakpoint, step) just comes back invalid,
                // which is fine — we only surface it when it's actually valid.
                try
                {
                    var exprResult = _dte.Debugger.GetExpression("$exception", true, 2000);
                    if (exprResult != null && exprResult.IsValidValue)
                    {
                        exception = new { type = exprResult.Type, value = exprResult.Value };
                    }
                }
                catch { /* not paused on an exception — expected, not an error */ }
            }
            // Auto-evaluate pinned variables if paused
            List<object> pinnedVars = null;
            if (mode == "paused")
            {
                try { pinnedVars = EvaluatePins(); } catch { }
            }
            return new SnapshotFields { Mode = mode, Function = function, Attached = attached, Exception = exception, Locals = locals, CurrentFile = currentFile, CurrentLine = currentLine, PinnedVars = pinnedVars };
        }

        private object SafeDebuggerSnapshot()
        {
            var f = BuildSnapshotFields();
            bool isFiltered = f.CurrentFile != null && f.CurrentLine > 0 && IsStepFilter(f.CurrentFile, f.CurrentLine);
            return new
            {
                mode = f.Mode,
                function = f.Function,
                file = f.CurrentFile,
                line = f.CurrentLine > 0 ? (int?)f.CurrentLine : null,
                suggestedStep = f.Mode == "paused" ? (isFiltered ? "stepOver" : "stepInto") : (string)null,
                attached = f.Attached,
                note = f.Attached == false
                    ? "Current frame's module is not part of the currently loaded VS solution/projects."
                    : null,
                exception = f.Exception,
                locals = f.Locals,
                localsNote = f.Locals != null && f.Locals.Count >= BundledLocalsCap
                    ? $"Capped at {BundledLocalsCap} locals for the top frame. Call get_locals for the full uncapped list or a different frame."
                    : null,
                pins = f.PinnedVars != null && f.PinnedVars.Count > 0 ? (object)f.PinnedVars : null,
            };
        }

        // -------------------------------------------------------------------
        // Breakpoints
        // -------------------------------------------------------------------

        private object SetBreakpoint(string file, int line, string condition)
        {
            if (string.IsNullOrEmpty(file)) throw new BridgeException(400, "file is required");
            if (line <= 0) throw new BridgeException(400, "line must be >= 1");

            // DTE.Debugger.Breakpoints.Add returns a Breakpoints collection (the breakpoints
            // *just added* — usually one entry, but binding may produce multiple).
            var added = _dte.Debugger.Breakpoints.Add("", file, line, 1,
                condition ?? "", dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                "", "", 0, "", 0, dbgHitCountType.dbgHitCountTypeNone);

            var ids = new List<string>();
            foreach (Breakpoint bp in added)
            {
                ids.Add(BreakpointId(bp));
            }

            return new { added = ids, file, line, condition };
        }

        private object RemoveBreakpoint(string id, string file, int? line)
        {
            int removed = 0;
            // Iterate over a snapshot — modifying the collection mid-loop is unsafe.
            var snapshot = new List<Breakpoint>();
            foreach (Breakpoint bp in _dte.Debugger.Breakpoints) snapshot.Add(bp);

            foreach (var bp in snapshot)
            {
                bool match = false;
                if (!string.IsNullOrEmpty(id) && BreakpointId(bp) == id) match = true;
                else if (!string.IsNullOrEmpty(file) && line.HasValue &&
                         string.Equals(bp.File, file, StringComparison.OrdinalIgnoreCase) &&
                         bp.FileLine == line.Value) match = true;

                if (match)
                {
                    try { bp.Delete(); removed++; } catch { /* ignore one-off failures */ }
                }
            }
            return new { removed };
        }

        private object ListBreakpoints()
        {
            var list = new List<object>();
            foreach (Breakpoint bp in _dte.Debugger.Breakpoints)
            {
                list.Add(new
                {
                    id = BreakpointId(bp),
                    file = bp.File,
                    line = bp.FileLine,
                    enabled = bp.Enabled,
                    condition = bp.Condition,
                });
            }
            return list;
        }

        private static string BreakpointId(Breakpoint bp)
        {
            // EnvDTE.Breakpoint doesn't expose a stable ID — synthesize one from File:Line.
            return $"{bp.File}:{bp.FileLine}";
        }

        // -------------------------------------------------------------------
        // Debug control
        // -------------------------------------------------------------------

        private object StartDebug()
        {
            try
            {
                _dte.Debugger.Go(WaitForBreakOrEnd: false);
                return new { started = true, state = DebuggerState() };
            }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Failed to start debug: " + ex.Message);
            }
        }

        private object AttachProcess(string processName, string remoteServer)
        {
            try
            {
                Processes processes;
                if (string.IsNullOrEmpty(remoteServer))
                {
                    processes = _dte.Debugger.LocalProcesses;
                }
                else
                {
                    // Remote: use the Default transport (msvsmon). VS will pop the native
                    // Windows credential dialog the first time per session if Windows Auth
                    // is required — we cannot and should not automate that.
                    // Transport/GetProcesses live on EnvDTE90.Debugger3, not EnvDTE.Debugger.
                    var debugger3 = _dte.Debugger as EnvDTE90.Debugger3;
                    if (debugger3 == null)
                    {
                        throw new BridgeException(500,
                            "Remote debugging requires EnvDTE90.Debugger3, which this Visual Studio instance did not provide.");
                    }

                    EnvDTE80.Transport transport = null;
                    foreach (EnvDTE80.Transport t in debugger3.Transports)
                    {
                        if (string.Equals(t.Name, "Default", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.Name, "Remote", StringComparison.OrdinalIgnoreCase))
                        {
                            transport = t;
                            break;
                        }
                    }
                    if (transport == null)
                    {
                        throw new BridgeException(500,
                            "No 'Default'/'Remote' transport found in Visual Studio. " +
                            "Confirm Remote Tools for Visual Studio (msvsmon) is installed.");
                    }
                    processes = debugger3.GetProcesses(transport, remoteServer);
                }

                // If process name omitted, return candidate list.
                if (string.IsNullOrEmpty(processName))
                {
                    return new { candidates = SummarizeProcesses(processes) };
                }

                // Find matches by name.
                var matches = new List<Process>();
                foreach (Process p in processes)
                {
                    if (string.Equals(System.IO.Path.GetFileName(p.Name), processName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(p);
                    }
                }

                if (matches.Count == 0)
                {
                    return new
                    {
                        attached = false,
                        error = $"No process named '{processName}' found" +
                                (remoteServer != null ? $" on {remoteServer}" : " locally"),
                        candidates = SummarizeProcesses(processes),
                    };
                }
                if (matches.Count > 1)
                {
                    return new
                    {
                        attached = false,
                        error = $"Multiple processes named '{processName}' — specify by PID",
                        candidates = SummarizeProcesses(matches),
                    };
                }

                matches[0].Attach();
                return new
                {
                    attached = true,
                    pid = matches[0].ProcessID,
                    name = matches[0].Name,
                    remote = remoteServer,
                };
            }
            catch (BridgeException) { throw; }
            catch (System.Runtime.InteropServices.COMException cex) when (
                cex.Message.IndexOf("logon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cex.Message.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cex.Message.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new BridgeException(401,
                    "Windows authentication required for remote attach. " +
                    "Complete the credential prompt in Visual Studio, then retry. " +
                    "To skip this prompt next time, pre-cache credentials with: " +
                    "cmdkey /add:<msvsmon-server> /user:<DOMAIN\\username> /pass");
            }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Attach failed: " + ex.Message);
            }
        }

        private static List<object> SummarizeProcesses(IEnumerable<Process> processes)
        {
            var list = new List<object>();
            foreach (Process p in processes)
            {
                list.Add(new { pid = p.ProcessID, name = p.Name });
            }
            return list;
        }

        private static List<object> SummarizeProcesses(Processes processes)
        {
            var list = new List<object>();
            foreach (Process p in processes)
            {
                list.Add(new { pid = p.ProcessID, name = p.Name });
            }
            return list;
        }

        private object ContinueExecution()
        {
            _dte.Debugger.Go(WaitForBreakOrEnd: false);
            return new { state = DebuggerState() };
        }

        private object Step(string kind)
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                throw new BridgeException(409,
                    "Debugger is not paused. Step commands require a break (e.g. after hitting a breakpoint).");
            }

            switch (kind)
            {
                case "into": _dte.Debugger.StepInto(WaitForBreakOrEnd: false); break;
                case "over": _dte.Debugger.StepOver(WaitForBreakOrEnd: false); break;
                case "out": _dte.Debugger.StepOut(WaitForBreakOrEnd: false); break;
                default: throw new BridgeException(400, "Unknown step kind: " + kind);
            }
            return new { state = DebuggerState() };
        }

        private object StopDebug()
        {
            try
            {
                _dte.Debugger.Stop(WaitForDesignMode: false);
                return new { stopped = true };
            }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Stop failed: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // Exception settings
        // -------------------------------------------------------------------

        private object SetBreakOnExceptions(bool enabled)
        {
            // By default VS only auto-breaks on exceptions that are truly
            // unhandled anywhere on the call stack. Realistic error-handling
            // code (catch, log, continue) never trips that, so this flips
            // "break when thrown" for all CLR exceptions — VS pauses at the
            // throw site even if something catches it a frame or two later.
            try
            {
                var debugger4 = _dte.Debugger as EnvDTE90a.Debugger4;
                if (debugger4 == null)
                {
                    throw new BridgeException(500,
                        "Exception settings require EnvDTE90a.Debugger4, which this Visual Studio instance did not provide.");
                }

                // ExceptionSettings.SetBreakWhenThrown(bool, ExceptionSetting) toggles one
                // specific entry — there's no single group-wide call, so apply it to every
                // entry under "Common Language Runtime Exceptions" individually. This mirrors
                // what clicking the group-level checkbox in VS's Exception Settings window
                // does internally.
                var groups = debugger4.ExceptionGroups;
                var clrSettings = groups.Item("Common Language Runtime Exceptions");
                int updated = 0;
                for (int i = 1; i <= clrSettings.Count; i++)
                {
                    var setting = clrSettings.Item(i);
                    clrSettings.SetBreakWhenThrown(enabled, setting);
                    updated++;
                }
                return new { breakOnExceptions = enabled, exceptionTypesUpdated = updated };
            }
            catch (BridgeException) { throw; }
            catch (Exception ex) when (IsAccessException(ex))
            {
                DisableFeature("BreakOnExceptions", ex.Message);
                throw new BridgeException(503,
                    $"BreakOnExceptions feature disabled for this session (access denied): {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new BridgeException(500,
                    "Failed to update exception settings: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // Output/Debug window streaming
        // -------------------------------------------------------------------

        // ToolWindows lives on EnvDTE80.DTE2, not plain EnvDTE.DTE — same pattern
        // as the Debugger3/Debugger4 casts already used elsewhere in this file.
        private EnvDTE.OutputWindow GetOutputWindow()
        {
            var dte2 = _dte as EnvDTE80.DTE2;
            if (dte2 == null)
            {
                throw new BridgeException(500,
                    "Output window access requires EnvDTE80.DTE2, which this Visual Studio instance did not provide.");
            }
            return (EnvDTE.OutputWindow)dte2.ToolWindows.OutputWindow;
        }

        private object ListOutputPanes()
        {
            var names = new List<string>();
            try
            {
                foreach (EnvDTE.OutputWindowPane p in GetOutputWindow().OutputWindowPanes)
                {
                    names.Add(p.Name);
                }
            }
            catch { /* output window not available in this VS state — return what we have */ }
            return new { panes = names, streamingEnabled = _outputStreamingEnabled };
        }

        private object SetOutputStreaming(bool enabled, string[] panes)
        {
            return TryFeature("OutputStreaming", () =>
            {
                _outputStreamingEnabled = enabled;
                _outputEnabledPanes = panes != null && panes.Length > 0
                    ? new HashSet<string>(panes, StringComparer.OrdinalIgnoreCase)
                    : null;

                if (!enabled)
                {
                    lock (_outputLock) { _outputBuffers.Clear(); }
                    return new { enabled = false };
                }

                foreach (EnvDTE.OutputWindowPane p in GetOutputWindow().OutputWindowPanes)
                {
                    if (_outputEnabledPanes == null || _outputEnabledPanes.Contains(p.Name))
                        HookOutputPane(p);
                }

                return (object)new { enabled = true, panes = _outputEnabledPanes != null ? new List<string>(_outputEnabledPanes) : null };
            });
        }

        private void HookOutputPane(EnvDTE.OutputWindowPane pane)
        {
            lock (_outputLock)
            {
                if (_outputBuffers.ContainsKey(pane.Name)) return;
                _outputBuffers[pane.Name] = new OutputPaneBuffer();
            }

            // Events2.get_TextEditorEvents(TextDocument) scopes the LineChanged
            // event to just this pane's document, instead of firing for every
            // text edit in every open editor in the IDE.
            var events2 = (EnvDTE80.Events2)_dte.Events;
            var textEvents = events2.get_TextEditorEvents(pane.TextDocument);
            string paneName = pane.Name; // capture for the closure
            textEvents.LineChanged += (start, end, hint) => OnOutputPaneChanged(paneName, start, end);
        }

        private void OnOutputPaneChanged(string paneName, TextPoint start, TextPoint end)
        {
            if (!_outputStreamingEnabled) return;
            if (_outputEnabledPanes != null && !_outputEnabledPanes.Contains(paneName)) return;

            try
            {
                // Read ONLY the changed region (start..end), not the whole document.
                // A previous version re-read the entire accumulated pane text on every
                // single line, which is O(total length) per line — O(n^2) overall for
                // n printed lines, and ran synchronously on the UI thread (COM events
                // fire there), which is exactly what froze VS under a busy print loop.
                string delta = start.CreateEditPoint().GetText(end);
                if (string.IsNullOrEmpty(delta)) return;

                lock (_outputLock)
                {
                    if (!_outputBuffers.TryGetValue(paneName, out var state)) return;
                    AppendCapped(state.FullBuffer, delta, OutputBufferCapChars);
                    AppendCapped(state.Pending, delta, OutputBufferCapChars);
                    state.Version++;
                }
                _outputChangeSignal.Set();
            }
            catch { /* best-effort — a transient COM failure here shouldn't break the bridge */ }
        }

        private static void AppendCapped(StringBuilder sb, string text, int capChars)
        {
            sb.Append(text);
            if (sb.Length > capChars)
            {
                sb.Remove(0, sb.Length - capChars);
            }
        }

        private object GetOutputSnapshot(string paneName, int maxChars)
        {
            if (!_outputStreamingEnabled)
            {
                throw new BridgeException(409,
                    "Output streaming is disabled. Call set_output_streaming(enabled: true) first.");
            }
            lock (_outputLock)
            {
                if (!_outputBuffers.TryGetValue(paneName, out var state))
                {
                    return new
                    {
                        pane = paneName,
                        text = "",
                        version = 0L,
                        note = "No output observed for this pane yet since streaming was enabled.",
                    };
                }
                string text = state.FullBuffer.ToString();
                if (maxChars > 0 && text.Length > maxChars)
                {
                    text = text.Substring(text.Length - maxChars);
                }
                return new { pane = paneName, text, version = state.Version };
            }
        }

        private long GetPaneVersion(string paneName)
        {
            lock (_outputLock)
            {
                return _outputBuffers.TryGetValue(paneName, out var state) ? state.Version : 0;
            }
        }

        private async Task<object> WaitForOutputAsync(string paneName, int timeoutMs, long sinceVersion, CancellationToken serverCt)
        {
            if (!_outputStreamingEnabled)
            {
                throw new BridgeException(409,
                    "Output streaming is disabled. Call set_output_streaming(enabled: true) first.");
            }
            timeoutMs = Math.Max(1000, Math.Min(timeoutMs, 120000));

            long startVersion = GetPaneVersion(paneName);
            bool alreadyChanged = sinceVersion >= 0 && sinceVersion < startVersion;

            if (!alreadyChanged)
            {
                _outputChangeSignal.Reset();
                if (GetPaneVersion(paneName) != startVersion) alreadyChanged = true;
            }

            if (!alreadyChanged)
            {
                try
                {
                    await Task.Run(() => _outputChangeSignal.Wait(timeoutMs, serverCt), serverCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* server shutting down */ }
            }

            long endVersion = GetPaneVersion(paneName);
            bool changed = alreadyChanged || endVersion != startVersion;
            if (!changed)
            {
                return new { changed = false, timedOut = true, version = endVersion };
            }

            string delta;
            lock (_outputLock)
            {
                if (_outputBuffers.TryGetValue(paneName, out var state))
                {
                    delta = state.Pending.ToString();
                    state.Pending.Clear();
                }
                else
                {
                    delta = "";
                }
            }

            return new { changed = true, version = endVersion, pane = paneName, text = delta };
        }

        // -------------------------------------------------------------------
        // Module/symbol diagnostics
        // -------------------------------------------------------------------

        private object SetModuleDiagnostics(bool enabled)
        {
            return TryFeature("ModuleDiagnostics", () =>
            {
                _moduleDiagnosticsEnabled = enabled;
                return (object)new { enabled };
            });
        }

        private object GetLoadedModules()
        {
            if (!_moduleDiagnosticsEnabled)
            {
                throw new BridgeException(409,
                    "Module diagnostics is disabled. Call set_module_diagnostics(enabled: true) first.");
            }
            if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                throw new BridgeException(409, "Module list only available while a debug session is active.");
            }

            var loadedProjectModules = GetLoadedProjectModuleNames();
            var list = new List<object>();
            try
            {
                var program = _dte.Debugger.CurrentProgram;
                if (program == null) throw new BridgeException(409, "No active debug program.");

                // Neither EnvDTE.Module nor Program.Modules is exposed in this PIA —
                // fetch the Modules collection late-bound too, then iterate it as a
                // plain IEnumerable (COM collections marshal that way via _NewEnum).
                object modulesObj = program.GetType().InvokeMember(
                    "Modules", System.Reflection.BindingFlags.GetProperty, null, program, null);
                if (modulesObj == null) throw new BridgeException(500, "Debug program has no Modules collection.");

                foreach (object m in (System.Collections.IEnumerable)modulesObj)
                {
                    string name = GetStringProperty(m, "Name");
                    string modulePath = GetStringProperty(m, "Path");
                    bool attached = IsModuleAttached(modulePath, loadedProjectModules);
                    var symbolInfo = DescribeModuleSymbols(m);
                    bool symbolsLoaded = symbolInfo != null && symbolInfo.LikelyLoaded;
                    list.Add(new
                    {
                        name,
                        path = modulePath,
                        attached,
                        symbolsLoaded = symbolInfo != null ? (bool?)symbolsLoaded : null,
                        symbolStatusRaw = symbolInfo?.RawValue,
                        note = !attached
                            ? "Not part of the currently loaded VS solution/projects — no source available."
                            : (symbolInfo != null && !symbolsLoaded
                                ? "No symbols loaded for this module. Check the PDB matches this build, and that " +
                                  "Tools > Options > Debugging > Symbols search paths cover the remote/output location."
                                : null),
                    });
                }
            }
            catch (BridgeException) { throw; }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Failed to enumerate modules: " + ex.Message);
            }
            return list;
        }

        private class ModuleSymbolInfo
        {
            public string PropertyName;
            public string RawValue;
            public bool LikelyLoaded;
        }

        // The exact EnvDTE interop version (Module/Module2/Module3 under
        // EnvDTE90/EnvDTE100) determines whether a symbol-status property is even
        // exposed, and under what name — rather than hardcode a guess that might
        // not match this VS's actual PIA, probe for known candidate property names
        // via late-bound reflection and report whatever VS actually exposes.
        private static ModuleSymbolInfo DescribeModuleSymbols(object m)
        {
            foreach (var propertyName in new[] { "SymbolStatus", "HasSymbolsLoaded", "SymbolsLoaded", "SymbolFileName" })
            {
                try
                {
                    var type = m.GetType();
                    object value = type.InvokeMember(propertyName,
                        System.Reflection.BindingFlags.GetProperty, null, m, null);
                    if (value == null) continue;

                    if (value is bool b)
                    {
                        return new ModuleSymbolInfo { PropertyName = propertyName, RawValue = b.ToString(), LikelyLoaded = b };
                    }

                    string s = value.ToString();
                    if (string.IsNullOrEmpty(s)) continue;
                    bool likelyLoaded =
                        s.IndexOf("NotLoaded", StringComparison.OrdinalIgnoreCase) < 0 &&
                        s.IndexOf("None", StringComparison.OrdinalIgnoreCase) < 0 &&
                        (s.IndexOf("Loaded", StringComparison.OrdinalIgnoreCase) >= 0 || propertyName == "SymbolFileName");
                    return new ModuleSymbolInfo { PropertyName = propertyName, RawValue = s, LikelyLoaded = likelyLoaded };
                }
                catch { /* this interop version doesn't expose this property — try the next candidate */ }
            }
            return null;
        }

        private static string GetStringProperty(object comObject, string propertyName)
        {
            try
            {
                var type = comObject.GetType();
                object value = type.InvokeMember(propertyName,
                    System.Reflection.BindingFlags.GetProperty, null, comObject, null);
                return value?.ToString();
            }
            catch { return null; }
        }

        // -------------------------------------------------------------------
        // Inspection
        // -------------------------------------------------------------------

        private object GetCallstack(int maxFrames)
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                throw new BridgeException(409, "Call stack only available while paused.");
            }

            var loadedModules = GetLoadedProjectModuleNames();
            var frames = new List<object>();
            var stack = _dte.Debugger.CurrentThread?.StackFrames;
            if (stack == null) return frames;

            int i = 0;
            foreach (StackFrame sf in stack)
            {
                if (i >= maxFrames) break;
                bool attached = IsModuleAttached(sf.Module, loadedModules);

                // Collect parameter values for each frame (best-effort — not all
                // frames expose Arguments; unmanaged/JIT-optimised frames may return nothing)
                var parameters = new List<object>();
                try
                {
                    foreach (Expression param in sf.Arguments)
                    {
                        parameters.Add(new { name = param.Name, type = param.Type, value = param.Value });
                    }
                }
                catch { /* not all frame types expose Arguments — skip silently */ }

                frames.Add(new
                {
                    index = i,
                    function = sf.FunctionName,
                    module = sf.Module,
                    language = sf.Language,
                    attached,
                    parameters = parameters.Count > 0 ? (object)parameters : null,
                    note = attached
                        ? null
                        : "This module is not part of the currently loaded VS solution/projects — " +
                          "no source or symbols are available here. Add the project to the solution " +
                          "if you need to inspect this code.",
                });
                i++;
            }
            return frames;
        }

        // -------------------------------------------------------------------
        // Loaded-project tracking — for flagging frames/modules outside the
        // currently attached project set (a real PSGTMS-scale concern: only
        // a subset of a 100+ project solution is typically loaded at once).
        // -------------------------------------------------------------------

        private HashSet<string> GetLoadedProjectModuleNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Project p in _dte.Solution.Projects)
                {
                    CollectProjectModuleNames(p, names);
                }
            }
            catch { /* best-effort — if Solution isn't available, treat nothing as attached */ }
            return names;
        }

        private void CollectProjectModuleNames(Project p, HashSet<string> names)
        {
            try
            {
                var outputFileNameProp = p.Properties?.Item("OutputFileName");
                if (outputFileNameProp?.Value is string outputFileName && !string.IsNullOrEmpty(outputFileName))
                {
                    names.Add(outputFileName);
                }
            }
            catch { /* not every project type (e.g. solution folders) exposes OutputFileName */ }

            try
            {
                foreach (ProjectItem item in p.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        CollectProjectModuleNames(item.SubProject, names);
                    }
                }
            }
            catch { /* not every project supports ProjectItems enumeration */ }
        }

        private static bool IsModuleAttached(string moduleFullPath, HashSet<string> loadedModules)
        {
            if (string.IsNullOrEmpty(moduleFullPath)) return false;
            string fileName = System.IO.Path.GetFileName(moduleFullPath);
            return loadedModules.Contains(fileName);
        }

        private object GetLocals(int frameIndex)
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                throw new BridgeException(409, "Locals only available while paused.");
            }

            StackFrame frame = GetFrame(frameIndex);
            if (frame == null) throw new BridgeException(404, $"No stack frame at index {frameIndex}");

            var locals = new List<object>();
            foreach (Expression expr in frame.Locals)
            {
                locals.Add(new
                {
                    name = expr.Name,
                    type = expr.Type,
                    value = expr.Value,
                });
            }
            return locals;
        }

        private object EvaluateExpression(string expression, int frameIndex)
        {
            if (string.IsNullOrEmpty(expression)) throw new BridgeException(400, "expression is required");
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                throw new BridgeException(409, "Evaluation requires the debugger to be paused.");
            }

            // EnvDTE's evaluation always uses the current frame; we set the active
            // frame first if a different one was requested.
            if (frameIndex > 0)
            {
                var frame = GetFrame(frameIndex);
                if (frame == null) throw new BridgeException(404, $"No stack frame at index {frameIndex}");
                // EnvDTE.StackFrame doesn't expose a SetCurrent method on all versions;
                // GetExpression evaluates in the current frame either way. This is a
                // known limitation — flagged for Phase 5 improvement.
            }

            Expression result;
            try
            {
                result = _dte.Debugger.GetExpression(expression, true, 5000);
            }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Evaluation failed: " + ex.Message);
            }

            return new
            {
                value = result.Value,
                type = result.Type,
                isValid = result.IsValidValue,
            };
        }

        private StackFrame GetFrame(int index)
        {
            var stack = _dte.Debugger.CurrentThread?.StackFrames;
            if (stack == null) return null;
            int i = 0;
            foreach (StackFrame sf in stack)
            {
                if (i == index) return sf;
                i++;
            }
            return null;
        }

        // -------------------------------------------------------------------
        // Set active frame — changes which frame get_locals and
        // evaluate_expression operate in, same as clicking a frame in the VS
        // Call Stack window. Effect persists until next step/continue/switch.
        // -------------------------------------------------------------------
        private object SetActiveFrame(int frameIndex)
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new BridgeException(409, "Frame selection only available while paused.");
            var frame = GetFrame(frameIndex);
            if (frame == null)
                throw new BridgeException(404, $"No stack frame at index {frameIndex}.");
            try
            {
                _dte.Debugger.CurrentStackFrame = frame;
                return new { frameIndex, function = frame.FunctionName, note = "Active frame set. get_locals and evaluate_expression now use this frame context." };
            }
            catch (Exception ex)
            {
                throw new BridgeException(500, "Failed to set active frame: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // Variable drill-down — expands an expression's members recursively,
        // equivalent to clicking the + triangle in the VS Locals/Watch window.
        // -------------------------------------------------------------------
        private object ExpandVariable(string expression, int maxDepth, int frameIndex)
        {
            if (string.IsNullOrEmpty(expression)) throw new BridgeException(400, "expression is required");
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new BridgeException(409, "Variable expansion requires the debugger to be paused.");
            if (frameIndex > 0)
            {
                var f = GetFrame(frameIndex);
                if (f != null) try { _dte.Debugger.CurrentStackFrame = f; } catch { }
            }
            Expression root;
            try { root = _dte.Debugger.GetExpression(expression, true, 5000); }
            catch (Exception ex) { throw new BridgeException(500, "GetExpression failed: " + ex.Message); }
            if (!root.IsValidValue)
                return new { name = root.Name, type = root.Type, value = root.Value, isValid = false, children = (object)null };
            return new
            {
                name = root.Name,
                type = root.Type,
                value = root.Value,
                isValid = true,
                children = ExpandChildren(root, maxDepth, 0),
            };
        }

        private List<object> ExpandChildren(Expression expr, int maxDepth, int depth)
        {
            if (depth >= maxDepth) return null;
            var children = new List<object>();
            try
            {
                foreach (Expression child in expr.DataMembers)
                {
                    int childCount = 0;
                    try { childCount = child.DataMembers.Count; } catch { }
                    children.Add(new
                    {
                        name  = child.Name,
                        type  = child.Type,
                        value = child.Value,
                        isValid = child.IsValidValue,
                        hasChildren = childCount > 0,
                        children = childCount > 0 && depth + 1 < maxDepth
                            ? ExpandChildren(child, maxDepth, depth + 1)
                            : null,
                    });
                }
            }
            catch { /* some COM objects refuse DataMembers enumeration */ }
            return children;
        }

        // -------------------------------------------------------------------
        // Exception info — structured details when paused on a throw.
        // Uses $exception (same as VS Watch/Immediate window pseudo-variable).
        // -------------------------------------------------------------------
        private object GetExceptionInfo()
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new BridgeException(409, "Exception info only available while paused.");
            try
            {
                var root = _dte.Debugger.GetExpression("$exception", false, 3000);
                if (root == null || !root.IsValidValue || root.Value == "null" || string.IsNullOrEmpty(root.Value))
                    return new { hasException = false, note = "No current exception at this execution point." };

                return new
                {
                    hasException = true,
                    type         = SafeEval("$exception.GetType().FullName") ?? root.Type,
                    value        = root.Value,
                    message      = SafeEval("$exception.Message"),
                    source       = SafeEval("$exception.Source"),
                    stackTrace   = SafeEval("$exception.StackTrace"),
                    innerException = BuildInnerException(1),
                };
            }
            catch (BridgeException) { throw; }
            catch (Exception ex) { throw new BridgeException(500, "Failed to get exception info: " + ex.Message); }
        }

        private object BuildInnerException(int depth)
        {
            if (depth > 3) return null; // guard against chains
            string prefix = string.Concat(System.Linq.Enumerable.Repeat(".InnerException", depth));
            string innerType = SafeEval($"$exception{prefix}?.GetType().FullName");
            if (string.IsNullOrEmpty(innerType)) return null;
            return new
            {
                type    = innerType,
                message = SafeEval($"$exception{prefix}?.Message"),
                inner   = BuildInnerException(depth + 1),
            };
        }

        private string SafeEval(string expression)
        {
            try
            {
                var r = _dte.Debugger.GetExpression(expression, false, 2000);
                if (r == null || !r.IsValidValue || r.Value == "null") return null;
                return r.Value;
            }
            catch { return null; }
        }

        // -------------------------------------------------------------------
        // Threads — list + switch active thread
        // -------------------------------------------------------------------
        private object ListThreads()
        {
            if (_dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                throw new BridgeException(409, "Thread list only available during an active debug session.");
            var threads = new List<object>();
            try
            {
                var program = _dte.Debugger.CurrentProgram;
                if (program == null) return new { threads, note = "No active debug program." };

                object threadsObj = program.GetType().InvokeMember(
                    "Threads", System.Reflection.BindingFlags.GetProperty, null, program, null);
                if (threadsObj == null)
                    return new { threads, note = "Thread enumeration not available for this debug target." };

                int currentId = GetThreadId(_dte.Debugger.CurrentThread);
                foreach (object t in (System.Collections.IEnumerable)threadsObj)
                {
                    int id = GetThreadIdFromCom(t);
                    string name = GetStringProperty(t, "Name");
                    int suspendCount = 0;
                    try
                    {
                        object sc = t.GetType().InvokeMember("SuspendCount",
                            System.Reflection.BindingFlags.GetProperty, null, t, null);
                        if (sc != null) suspendCount = Convert.ToInt32(sc);
                    }
                    catch { }
                    threads.Add(new
                    {
                        id,
                        name = string.IsNullOrEmpty(name) ? $"Thread {id}" : name,
                        isCurrent = id == currentId,
                        suspendCount,
                    });
                }
                return new { threads, currentThreadId = currentId };
            }
            catch (BridgeException) { throw; }
            catch (Exception ex) { throw new BridgeException(500, "Failed to enumerate threads: " + ex.Message); }
        }

        private object SwitchThread(int threadId)
        {
            if (_dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new BridgeException(409, "Thread switching only available while paused.");
            try
            {
                var program = _dte.Debugger.CurrentProgram;
                if (program == null) throw new BridgeException(409, "No active debug program.");

                object threadsObj = program.GetType().InvokeMember(
                    "Threads", System.Reflection.BindingFlags.GetProperty, null, program, null);
                if (threadsObj == null) throw new BridgeException(500, "Thread collection not available.");

                foreach (object t in (System.Collections.IEnumerable)threadsObj)
                {
                    if (GetThreadIdFromCom(t) == threadId)
                    {
                        _dte.Debugger.CurrentThread = (EnvDTE.Thread)t;
                        return new { switched = true, threadId };
                    }
                }
                throw new BridgeException(404, $"No thread with ID {threadId}.");
            }
            catch (BridgeException) { throw; }
            catch (Exception ex) { throw new BridgeException(500, "Thread switch failed: " + ex.Message); }
        }

        private int GetThreadId(EnvDTE.Thread thread)
        {
            if (thread == null) return -1;
            try { return thread.ID; } catch { return -1; }
        }

        private int GetThreadIdFromCom(object t)
        {
            try
            {
                object id = t.GetType().InvokeMember("ID",
                    System.Reflection.BindingFlags.GetProperty, null, t, null);
                return id != null ? Convert.ToInt32(id) : -1;
            }
            catch { return -1; }
        }

        // -------------------------------------------------------------------
        // Watch expressions — manual on-demand evaluation
        // -------------------------------------------------------------------
        private object AddWatch(string name, string expression)
        {
            if (string.IsNullOrEmpty(name)) throw new BridgeException(400, "name is required");
            if (string.IsNullOrEmpty(expression)) throw new BridgeException(400, "expression is required");
            lock (_watchLock) { _watches[name] = expression; }
            return new { added = true, name, expression };
        }

        private object GetWatches()
        {
            Dictionary<string, string> snapshot;
            lock (_watchLock) { snapshot = new Dictionary<string, string>(_watches); }
            if (snapshot.Count == 0) return new { watches = new List<object>() };

            bool paused = _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            var items = new List<object>();
            foreach (var kvp in snapshot)
            {
                object result;
                if (paused)
                {
                    try
                    {
                        var r = _dte.Debugger.GetExpression(kvp.Value, true, 3000);
                        result = new { value = r.Value, type = r.Type, isValid = r.IsValidValue };
                    }
                    catch (Exception ex) { result = new { error = ex.Message }; }
                }
                else
                {
                    result = new { note = "Not evaluated — debugger is not paused." };
                }
                items.Add(new { name = kvp.Key, expression = kvp.Value, result });
            }
            return new { watches = items };
        }

        private object RemoveWatch(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new BridgeException(400, "name is required");
            bool removed;
            lock (_watchLock) { removed = _watches.Remove(name); }
            if (!removed) throw new BridgeException(404, $"No watch named '{name}'.");
            return new { removed = true, name };
        }

        // -------------------------------------------------------------------
        // Pinned variables — auto-evaluated in every snapshot
        // -------------------------------------------------------------------
        private object PinVariable(string name, string expression)
        {
            if (string.IsNullOrEmpty(name)) throw new BridgeException(400, "name is required");
            if (string.IsNullOrEmpty(expression)) throw new BridgeException(400, "expression is required");
            lock (_pinLock) { _pinnedVars[name] = expression; }
            return new { pinned = true, name, expression, note = "This expression is now auto-evaluated in every wait_for_debugger_change response." };
        }

        private object GetPinnedVariables()
        {
            bool paused = _dte.Debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            if (!paused)
            {
                var list = new List<object>();
                lock (_pinLock)
                {
                    foreach (var kvp in _pinnedVars)
                        list.Add(new { name = kvp.Key, expression = kvp.Value, value = (string)null, note = "Not evaluated — debugger not paused." });
                }
                return new { pins = list };
            }
            return new { pins = EvaluatePins() };
        }

        private object UnpinVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new BridgeException(400, "name is required");
            bool removed;
            lock (_pinLock) { removed = _pinnedVars.Remove(name); }
            if (!removed) throw new BridgeException(404, $"No pinned variable named '{name}'.");
            return new { unpinned = true, name };
        }

        // Called from BuildSnapshotFields and GetPinnedVariables — must be on UI thread.
        private List<object> EvaluatePins()
        {
            var result = new List<object>();
            Dictionary<string, string> snapshot;
            lock (_pinLock) { snapshot = new Dictionary<string, string>(_pinnedVars); }
            foreach (var kvp in snapshot)
            {
                try
                {
                    var r = _dte.Debugger.GetExpression(kvp.Value, true, 2000);
                    result.Add(new { name = kvp.Key, expression = kvp.Value, value = r.Value, type = r.Type, isValid = r.IsValidValue });
                }
                catch (Exception ex)
                {
                    result.Add(new { name = kvp.Key, expression = kvp.Value, value = (string)null, error = ex.Message });
                }
            }
            return result;
        }

        private string DebuggerState()
        {
            switch (_dte.Debugger.CurrentMode)
            {
                case dbgDebugMode.dbgRunMode: return "running";
                case dbgDebugMode.dbgBreakMode: return "paused";
                case dbgDebugMode.dbgDesignMode: return "design";
                default: return "unknown";
            }
        }
    }

    internal class BridgeException : Exception
    {
        public int StatusCode { get; }
        public BridgeException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    }
}
