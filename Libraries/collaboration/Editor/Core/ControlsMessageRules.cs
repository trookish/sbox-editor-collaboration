namespace TeamCreate;

public static class ControlsMessageRules
{
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
