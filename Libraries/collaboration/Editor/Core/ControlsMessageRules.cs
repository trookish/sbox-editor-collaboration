namespace TeamCreate;

public static class ControlsMessageRules
{
    /// <summary>
    /// True when the message reports that something went wrong: a teardown error, a session error, or a recovery notice. A saved, undone or auto-saved line is news, not
    /// a problem, so it is not painted as one and does not keep the Controls card on screen after the session ends.
    /// </summary>
    public static bool IsProblem(string terminal, string error, string recoveryNotice)
    {
        return terminal != null || error != null || recoveryNotice != null;
    }

    public static string Compose(string terminal, string error, string recoveryPath, string recoveryNotice, string actionNotice, string undoNotice, string saveNotice)
    {
        if (terminal != null)
        {
            return terminal;
        }
        if (error != null)
        {
            // A recovery line with nothing after it reads as a broken promise; it appears only when an archive exists.
            string text = string.IsNullOrEmpty(recoveryPath) ? error : (error + "\nRecovery: " + recoveryPath);
            return (actionNotice == null) ? text : (text + "\n" + actionNotice);
        }
        return recoveryNotice ?? actionNotice ?? undoNotice ?? saveNotice ?? string.Empty;
    }
}
