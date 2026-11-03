using System;

namespace TeamCreate;

/// <summary>
/// Changes local bookkeeping (an undo entry, a pending edit) only once the edit that depends on it was handed to the connection. A send
/// that throws leaves the bookkeeping as it was, runs the rollback and passes the failure on, so the step can be taken again later.
/// </summary>
public static class EditCommit
{
    public static void AfterSend( Action send, Action commit, Action rollback = null )
    {
        try
        {
            send();
        }
        catch
        {
            rollback?.Invoke();
            throw;
        }
        commit();
    }
}
