using System;

namespace TeamCreate;

public sealed class ActionRefusal : InvalidOperationException
{
    public ActionRefusal(string message)
        : base(message)
    {
    }
}

public static class ActionRefusalRules
{
    public static bool IsRefusal(Exception error)
    {
        return error is ActionRefusal;
    }
}
