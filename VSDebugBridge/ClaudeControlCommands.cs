using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace VSDebugBridge
{
    /// <summary>
    /// Wires the "Claude Debug Control" Debug-menu commands (defined in
    /// VSCommandTable.vsct) to BridgeServer.SetControlModeFromUi. This is the
    /// ONLY code path that can change BridgeServer's control mode — these are
    /// ordinary VS command handlers, invoked by a human clicking a menu item
    /// or pressing a keyboard shortcut, never reachable from the HTTP bridge
    /// that the MCP server talks to.
    /// </summary>
    internal sealed class ClaudeControlCommands
    {
        private static readonly Guid CommandSet = new Guid("3F1A7B2E-5C9D-4E6A-8B3F-2D7E4A9C6F11");
        private const int CmdLetClaudeDebug = 0x0100;
        private const int CmdLetClaudeWatchDebug = 0x0101;
        private const int CmdPauseClaudeDebug = 0x0102;
        private const int CmdTakeOverDebug = 0x0103;
        private const int CmdStopClaudeDebug = 0x0104;
        private const int CmdMarkStepOver = 0x0105;
        private const int CmdUnmarkStepOver = 0x0106;

        private readonly BridgeServer _server;

        private ClaudeControlCommands(OleMenuCommandService commandService, BridgeServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));

            AddCommand(commandService, CmdLetClaudeDebug, BridgeServer.ControlMode.Driver);
            AddCommand(commandService, CmdLetClaudeWatchDebug, BridgeServer.ControlMode.Observer);
            AddCommand(commandService, CmdPauseClaudeDebug, BridgeServer.ControlMode.Paused);
            AddCommand(commandService, CmdTakeOverDebug, BridgeServer.ControlMode.UserDriving);
            AddCommand(commandService, CmdStopClaudeDebug, BridgeServer.ControlMode.EmergencyStop);
            AddStepFilterCommands(commandService);
        }

        private void AddCommand(OleMenuCommandService commandService, int cmdId, BridgeServer.ControlMode mode)
        {
            var commandId = new CommandID(CommandSet, cmdId);
            var command = new OleMenuCommand((sender, e) => _server.SetControlModeFromUi(mode), commandId);
            // Show a checkmark next to whichever mode is currently active, so the
            // menu itself is the "status indicator" for v1 (no separate status bar
            // picker yet — see VSCommandTable.vsct header comment).
            command.BeforeQueryStatus += (sender, e) =>
            {
                ((OleMenuCommand)sender).Checked = _server.CurrentControlMode == mode;
            };
            commandService.AddCommand(command);
        }

        private void AddStepFilterCommands(OleMenuCommandService commandService)
        {
            // Mark: visible when current line is NOT already marked.
            var markId = new CommandID(CommandSet, CmdMarkStepOver);
            var markCmd = new OleMenuCommand((sender, e) =>
            {
                var pos = _server.GetCurrentEditorPosition();
                if (pos.HasValue) _server.AddStepFilter(pos.Value.file, pos.Value.line);
            }, markId);
            markCmd.BeforeQueryStatus += (sender, e) =>
            {
                var cmd = (OleMenuCommand)sender;
                var pos = _server.GetCurrentEditorPosition();
                bool hasPos = pos.HasValue;
                bool alreadyMarked = hasPos && _server.IsStepFilter(pos.Value.file, pos.Value.line);
                cmd.Visible = hasPos && !alreadyMarked;
                cmd.Enabled = hasPos && !alreadyMarked;
            };
            commandService.AddCommand(markCmd);

            // Unmark: visible when current line IS already marked.
            var unmarkId = new CommandID(CommandSet, CmdUnmarkStepOver);
            var unmarkCmd = new OleMenuCommand((sender, e) =>
            {
                var pos = _server.GetCurrentEditorPosition();
                if (pos.HasValue) _server.RemoveStepFilter(pos.Value.file, pos.Value.line);
            }, unmarkId);
            unmarkCmd.BeforeQueryStatus += (sender, e) =>
            {
                var cmd = (OleMenuCommand)sender;
                var pos = _server.GetCurrentEditorPosition();
                bool alreadyMarked = pos.HasValue && _server.IsStepFilter(pos.Value.file, pos.Value.line);
                cmd.Visible = alreadyMarked;
                cmd.Enabled = alreadyMarked;
            };
            commandService.AddCommand(unmarkCmd);
        }

        internal static async Task InitializeAsync(AsyncPackage package, BridgeServer server)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "VSDebugBridge: IMenuCommandService unavailable; Claude Debug Control menu not installed.");
                return;
            }
            new ClaudeControlCommands(commandService, server);
        }
    }
}
