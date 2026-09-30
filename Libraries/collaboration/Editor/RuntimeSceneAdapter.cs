using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace TeamCreate;

public static class RuntimeSceneAdapter
{
    public const string UnsupportedMutationReason = "Runtime scene mutation is disabled until a tested runtime undo/apply strategy is proven.";

    public static RuntimeSessionDescriptor Describe(GameEditorSession session)
    {
        if (session == null)
        {
            return null;
        }
        return new RuntimeSessionDescriptor
        {
            Kind = RuntimeSessionKind.Game,
            SessionToken = session,
            ParentSessionToken = session.Parent,
            IsValid = (session.Scene != null && session.Scene.IsValid()),
            IsDestroyed = (session.Scene == null || !session.Scene.IsValid()),
            SourceScenePath = session.Parent?.Scene?.Source?.ResourcePath
        };
    }

    public static Dictionary<string, ObjectState> Capture(GameEditorSession session, SceneEditorSession expectedSource, string configuredSourcePath)
    {
        RuntimeSessionDescriptor runtimeSessionDescriptor = RuntimeSessionGuard.EnsureRuntimeSession(Describe(session), expectedSource, configuredSourcePath);
        Scene scene = session.Scene;
        if (scene == null || !scene.IsValid())
        {
            throw new InvalidOperationException("Runtime session is missing or destroyed.");
        }
        Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>(StringComparer.Ordinal);
        using (scene.Push())
        {
            GameObject[] array = scene.Children.ToArray();
            foreach (GameObject gameObject in array)
            {
                CaptureObject(gameObject, string.Empty, isRoot: true, result);
            }
        }
        return result;
    }

    public static RuntimeSceneApplyPlan PlanApply(GameEditorSession session, SceneEditorSession expectedSource, string configuredSourcePath, IReadOnlyDictionary<string, ObjectState> target)
    {
        RuntimeSessionGuard.EnsureRuntimeSession(Describe(session), expectedSource, configuredSourcePath);
        Scene scene = session.Scene;
        if (scene == null || !scene.IsValid())
        {
            throw new InvalidOperationException("Runtime session is missing or destroyed.");
        }
        Dictionary<string, ObjectState> before = Capture(session, expectedSource, configuredSourcePath);
        return RuntimeSceneTargetOnlyBoundary.Shared.PlanForAdapter(configuredSourcePath, before, target);
    }

    public static RuntimeSceneApplyPlan Apply(GameEditorSession session, SceneEditorSession expectedSource, string configuredSourcePath, IReadOnlyDictionary<string, ObjectState> target)
    {
        return PlanApply(session, expectedSource, configuredSourcePath, target);
    }

    private static void CaptureObject(GameObject gameObject, string parentId, bool isRoot, Dictionary<string, ObjectState> result)
    {
        RuntimeSceneCapture.EnsureRuntimeObjectEntry(gameObject, isRoot);
        if (gameObject.IsDestroyed)
        {
            throw new InvalidOperationException("Runtime object is destroyed or being destroyed: " + gameObject.Id);
        }
        if (!gameObject.IsValid())
        {
            throw new InvalidOperationException("Runtime object is invalid: " + gameObject.Id);
        }
        if (!gameObject.Flags.HasFlag(GameObjectFlags.NotSaved))
        {
            ObjectState objectState = RuntimeSceneCapture.CaptureObject(gameObject, gameObject.Id, gameObject.Serialize(), parentId, isRoot);
            if (result.ContainsKey(objectState.Id))
            {
                throw new InvalidOperationException("Runtime scene contains duplicate object id: " + objectState.Id);
            }
            result.Add(objectState.Id, objectState);
            GameObject[] array = gameObject.Children.ToArray();
            foreach (GameObject gameObject2 in array)
            {
                CaptureObject(gameObject2, objectState.Id, isRoot: false, result);
            }
        }
    }
}
