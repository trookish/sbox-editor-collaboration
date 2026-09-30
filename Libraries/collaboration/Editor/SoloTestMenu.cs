using System;
using System.Linq;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The same one-click test from the editor's menu bar, for people who do not have the Collaboration dock open.
/// In a real project it makes the disposable copy and opens it; inside a copy it runs the test.
/// </summary>
public static class SoloTestMenu
{
    [Menu("Editor", "Collaboration/Run solo test")]
    public static void Run()
    {
        try
        {
            if (SoloTestRunner.IsSoloCopy)
            {
                SoloTestRunner.Start();
                return;
            }
            string root = SceneAdapter.Root;
            if (CollaborationSession.Current.Busy)
            {
                EditorUtility.DisplayDialog("Solo test", "Leave the current collaboration session first.");
                return;
            }
            if (SceneEditorSession.All.Any((SceneEditorSession x) => !(x is GameEditorSession) && x.HasUnsavedChanges))
            {
                EditorUtility.DisplayDialog("Solo test", "Save your scene first, so the copy is made from what you see.");
                return;
            }
            string memory = SoloCopy.MemoryAdvice(SoloTestLauncher.AvailableMemoryBytes());
            if (memory != null)
            {
                EditorUtility.DisplayDialog("Solo test", memory);
                return;
            }
            if (SoloTestLauncher.IsRunning(SoloCopy.CopyRoot(root)))
            {
                EditorUtility.DisplayDialog("Solo test", "The solo test window is already open. Switch to it.");
                return;
            }
            SoloCopyResult copy = SoloCopy.Build(root, autorun: true);
            if (!copy.Ok)
            {
                EditorUtility.DisplayDialog("Solo test", copy.Error);
                return;
            }
            string error = SoloTestLauncher.Launch(copy.Path);
            EditorUtility.DisplayDialog("Solo test", error ?? "Opened a solo test window. The test runs there by itself in about a minute.");
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Solo test", "The solo test could not start: " + ex.Message);
        }
    }
}
