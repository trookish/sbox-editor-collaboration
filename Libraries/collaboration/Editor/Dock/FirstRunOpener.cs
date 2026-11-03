using System;
using Editor;
using Sandbox;
using TeamCreate;

/// <summary>
/// Opens the Collaboration dock the first time the library loads in a project (FirstRunRules), so a new user finds it without looking.
/// Remembered per project in an editor cookie; a person who closes the dock afterwards is not overruled.
/// </summary>
public static class FirstRunOpener
{
    private static bool checkedThisLoad;

    [EditorEvent.Frame]
    public static void Check()
    {
        if (checkedThisLoad)
        {
            return;
        }
        checkedThisLoad = true;
        try
        {
            string key = FirstRunRules.CookieKey(SceneAdapter.Root);
            bool opened = Sandbox.Internal.GlobalToolsNamespace.EditorCookie.Get(key, 0) == 1;
            if (!FirstRunRules.ShouldOpenDock(opened, SoloTestRunner.IsSoloCopy))
            {
                return;
            }
            Sandbox.Internal.GlobalToolsNamespace.EditorCookie.Set(key, 1);
            Sandbox.Internal.GlobalToolsNamespace.EditorWindow.DockManager.OpenDock("Collaboration", DockArea.Right);
        }
        catch (Exception)
        {
        }
    }
}
