using System;
using Editor;

namespace TeamCreate;

public sealed class ControlsCard : DockCard
{
    private readonly ConnectCard connect;

    private readonly Label message;

    private readonly Button save;

    private readonly Button undo;

    private readonly Button recover;

    private readonly Checkbox autoSave;

    private readonly Checkbox autoRecover;

    private SessionState lastState = (SessionState)(-1);

    private bool lastConnected;

    private bool lastBusy;

    private PlayModeLifecycleState lastLifecycleState = (PlayModeLifecycleState)(-1);

    private string lastTerminal;

    private string lastError;

    private string lastRecovery;

    private string lastPath;

    private string lastSaveNotice;

    private string lastUndoNotice;

    private string lastAction;

    private string lastMergeNotice;

    protected override bool FieldsAssigned => connect != null && message != null && save != null && undo != null && recover != null && autoSave != null && autoRecover != null;

    public ControlsCard(Widget parent, ConnectCard connect)
        : base(parent, "Controls")
    {
        ControlsCard controlsCard = this;
        this.connect = connect;
        message = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.BlockedColor
        });
        message.Visible = false;
        save = Layout.Add(new Button("Save synchronized scene", this)
        {
            Clicked = () =>
            {
                DockCard.Run(() =>
                {
                    CollaborationSession.Current.Save();
                });
            }
        });
        undo = Layout.Add(new Button("Undo my last collaboration operation", this)
        {
            Clicked = () =>
            {
                DockCard.Run(() =>
                {
                    CollaborationSession.Current.Undo();
                });
            }
        });
        recover = Layout.Add(new Button("Recover & reconnect", this)
        {
            ToolTip = "Archive local edits, reload the saved scenes from disk, then accept the host's current state.",
            Clicked = () =>
            {
                DockCard.Run(() =>
                {
                    CollaborationSession.Current.Reconnect(connect.Address, connect.PortNumber);
                });
            }
        });
        autoSave = Layout.Add(new Checkbox
        {
            Text = "Auto-save when idle",
            ToolTip = "Save the synchronized scene automatically once it has gone 30 seconds without a local edit, while no collaborator is reported editing and the editor is not in play mode, at most once every 30 seconds. Off by default."
        });
        Checkbox checkbox = autoSave;
        checkbox.Toggled = (Action)Delegate.Combine(checkbox.Toggled, (Action)(() =>
        {
            CollaborationSession.Current.AutoSaveIdle = controlsCard.autoSave.Value;
        }));
        autoRecover = Layout.Add(new Checkbox
        {
            Text = "Automatic recovery",
            ToolTip = "Automatic recovery reloads scenes from disk; unsaved local changes are archived first. Off by default."
        });
        Checkbox checkbox2 = autoRecover;
        checkbox2.Toggled = (Action)Delegate.Combine(checkbox2.Toggled, (Action)(() =>
        {
            CollaborationSession.Current.AutoRecover = controlsCard.autoRecover.Value;
        }));
    }

    /// <summary>True while the card is showing a problem (an error or a recovery notice), so the dock keeps the card on screen after a session ends until it has been read. A line that only reports a save or an undo does not count.</summary>
    public bool HasMessage => message != null && message.Visible && problem;

    private bool problem;

    private static void SetEnabled(Button button, bool enabled)
    {
        if (button.Enabled != enabled)
        {
            button.Enabled = enabled;
        }
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        string terminalTeardownError = current.TerminalTeardownError;
        string error = current.Error;
        string recoveryNotice = current.RecoveryNotice;
        string recoveryPath = current.RecoveryPath;
        string saveNotice = current.SaveNotice;
        string undoNotice = current.UndoNotice;
        string actionNotice = current.ActionNotice;
        string mergeNotice = current.MergeNotice;
        SessionState state = current.State;
        bool connected = current.Connected;
        bool busy = current.Busy;
        PlayModeLifecycleState state2 = current.RuntimeLifecycle.State;
        if (autoSave.Value != current.AutoSaveIdle)
        {
            autoSave.Value = current.AutoSaveIdle;
        }
        if (autoRecover.Value != current.AutoRecover)
        {
            autoRecover.Value = current.AutoRecover;
        }
        bool flag = state != lastState || connected != lastConnected || busy != lastBusy || state2 != lastLifecycleState || !string.Equals(terminalTeardownError, lastTerminal, StringComparison.Ordinal) || !string.Equals(error, lastError, StringComparison.Ordinal);
        if (!string.Equals(terminalTeardownError, lastTerminal, StringComparison.Ordinal) || !string.Equals(error, lastError, StringComparison.Ordinal) || !string.Equals(recoveryNotice, lastRecovery, StringComparison.Ordinal) || !string.Equals(recoveryPath, lastPath, StringComparison.Ordinal) || !string.Equals(saveNotice, lastSaveNotice, StringComparison.Ordinal) || !string.Equals(undoNotice, lastUndoNotice, StringComparison.Ordinal) || !string.Equals(actionNotice, lastAction, StringComparison.Ordinal) || !string.Equals(mergeNotice, lastMergeNotice, StringComparison.Ordinal))
        {
            lastTerminal = terminalTeardownError;
            lastError = error;
            lastRecovery = recoveryNotice;
            lastPath = recoveryPath;
            lastSaveNotice = saveNotice;
            lastUndoNotice = undoNotice;
            lastAction = actionNotice;
            lastMergeNotice = mergeNotice;
            DockCard.SetText(message, ControlsMessageRules.Compose(terminalTeardownError, error, recoveryPath, recoveryNotice, actionNotice, undoNotice, saveNotice, mergeNotice));
            message.Visible = message.Text.Length > 0;
            // Red is for something that went wrong. A line that only says what was done is shown quietly.
            problem = ControlsMessageRules.IsProblem(terminalTeardownError, error, recoveryNotice);
            message.Color = problem ? DockTheme.BlockedColor : DockTheme.MutedColor;
        }
        if (flag)
        {
            lastState = state;
            lastConnected = connected;
            lastBusy = busy;
            lastLifecycleState = state2;
            bool flag2 = connected;
            bool flag3 = flag2;
            if (flag3)
            {
                bool flag4 = ((state == SessionState.Hosting || state == SessionState.Connected) ? true : false);
                flag3 = flag4;
            }
            bool enabled = flag3;
            SetEnabled(save, enabled);
            SetEnabled(undo, enabled);
            Button button = recover;
            bool flag5 = error != null && terminalTeardownError == null && !current.RuntimeLifecycle.HasTerminalTeardownFailure;
            bool flag6 = flag5;
            if (flag6)
            {
                bool flag4 = ((state2 == PlayModeLifecycleState.RuntimeStarting || state2 == PlayModeLifecycleState.RuntimeBlocked) ? true : false);
                flag6 = !flag4;
            }
            SetEnabled(button, flag6);
        }
    }
}
