using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

[assembly: Microsoft.VisualStudio.Shell.ProvideCodeBase(AssemblyName = "VSDebugBridge")]

namespace VSDebugBridge
{
    /// <summary>
    /// VSIX entry point. Starts the HTTP bridge server when Visual Studio loads
    /// and stops it cleanly on shutdown.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("VS Debug Bridge for Claude Code", "HTTP bridge for the vs-debugger MCP server", "0.1.0")]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class DebugBridgePackage : AsyncPackage
    {
        public const string PackageGuidString = "8B5C6E1A-3D2F-4A7C-9F8E-1B6D4A2E5C90";
        private const int DefaultBasePort = 5959;

        private BridgeServer _server;
        private string _instanceFilePath;
        private int _boundPort;
        private EnvDTE.DTE _dte;
        private EnvDTE.SolutionEvents _solutionEvents;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switch to UI thread once at startup so we can grab the DTE service safely.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dte = (EnvDTE.DTE)await GetServiceAsync(typeof(EnvDTE.DTE));
            if (dte == null)
            {
                // Without DTE, we can't do anything useful — but don't crash VS.
                System.Diagnostics.Debug.WriteLine("VSDebugBridge: DTE service unavailable; bridge not started.");
                return;
            }
            _dte = dte;

            // The package can auto-load before any solution is open (see the
            // NoSolution_string UI context above), so the solution name at
            // InitializeAsync time is frequently empty. Re-write the instance
            // file whenever a solution opens/closes so list_vs_instances stays
            // accurate instead of being frozen at whatever was true at startup.
            // Keep a strong reference — same reason as BridgeServer's DebuggerEvents
            // field: an unreferenced COM event sink gets garbage collected and
            // events silently stop firing.
            _solutionEvents = dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionChanged;
            _solutionEvents.AfterClosing += OnSolutionChanged;

            // Base port is configurable via VSDEBUGBRIDGE_PORT (mirrors the MCP
            // server's VS_BRIDGE_URL override). Multiple simultaneous VS
            // instances now each get their own port automatically — Start()
            // tries basePort, basePort+1, ... instead of giving up if the first
            // one is already taken by another instance's bridge.
            int basePort = DefaultBasePort;
            var portEnv = Environment.GetEnvironmentVariable("VSDEBUGBRIDGE_PORT");
            if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out var parsedPort))
            {
                basePort = parsedPort;
            }

            _server = new BridgeServer(dte, JoinableTaskFactory);
            try
            {
                _boundPort = _server.Start(basePort);
                System.Diagnostics.Debug.WriteLine($"VSDebugBridge: HTTP listener started on http://localhost:{_boundPort}/");
                WriteInstanceFile(_boundPort, dte);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"VSDebugBridge: could not bind any port starting at {basePort} — this VS instance " +
                    "will not have an active bridge. " + ex.Message);
                _server = null;
            }

            if (_server != null)
            {
                await ClaudeControlCommands.InitializeAsync(this, _server);
            }
        }

        private void OnSolutionChanged()
        {
            if (_server != null && _dte != null)
            {
                WriteInstanceFile(_boundPort, _dte);
                _server.LoadStepFilters();
                _server.LoadJournal();
            }
        }

        // Writes a small discovery file so the MCP server can find this
        // instance's port without it being hardcoded — one file per running
        // VS instance (named by process ID), cleaned up on shutdown. See
        // mcp-server/index.js for the reader side.
        private void WriteInstanceFile(int port, EnvDTE.DTE dte)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VSDebugBridge", "instances");
                Directory.CreateDirectory(dir);

                string solutionName = "(none)";
                try
                {
                    if (!string.IsNullOrEmpty(dte.Solution?.FullName))
                    {
                        solutionName = Path.GetFileNameWithoutExtension(dte.Solution.FullName);
                    }
                }
                catch { /* solution may not be loaded yet — fine, leave as "(none)" */ }

                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                _instanceFilePath = Path.Combine(dir, $"{pid}.json");
                string json = $"{{\"pid\":{pid},\"port\":{port},\"solution\":\"{solutionName.Replace("\"", "'")}\",\"startedAt\":\"{DateTime.UtcNow:O}\"}}";
                File.WriteAllText(_instanceFilePath, json);
            }
            catch { /* discovery file is best-effort — don't block bridge startup over it */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _solutionEvents != null)
            {
                try
                {
                    _solutionEvents.Opened -= OnSolutionChanged;
                    _solutionEvents.AfterClosing -= OnSolutionChanged;
                }
                catch { /* don't block VS shutdown */ }
                _solutionEvents = null;
            }
            if (disposing && _server != null)
            {
                try { _server.Stop(); } catch { /* don't block VS shutdown */ }
                _server = null;
            }
            if (disposing && _instanceFilePath != null)
            {
                try { File.Delete(_instanceFilePath); } catch { /* best-effort cleanup */ }
                _instanceFilePath = null;
            }
            base.Dispose(disposing);
        }
    }
}
