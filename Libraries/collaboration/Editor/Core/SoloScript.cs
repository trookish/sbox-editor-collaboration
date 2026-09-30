using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

public enum SoloStepState
{
    Waiting,
    Running,
    Passed,
    Failed,
    Skipped
}

/// <summary>One line of the solo test's checklist, worded for the person watching the dock.</summary>
public sealed class SoloStep
{
    public string Id;

    public string Title;

    public SoloStepState State = SoloStepState.Waiting;

    public string Detail;
}

public enum SoloHostReadiness
{
    Waiting,
    Ready,
    Blocked
}

public enum SoloStopDecision
{
    Wait,
    Proceed
}

public enum SoloSceneLoad
{
    Unknown,
    Loading,
    Loaded
}

public static class SoloReport
{
    public static string Symbol(SoloStepState state)
    {
        switch (state)
        {
            case SoloStepState.Passed:
                return "✓";
            case SoloStepState.Failed:
                return "✗";
            case SoloStepState.Running:
                return "…";
            case SoloStepState.Skipped:
                return "–";
            default:
                return "·";
        }
    }

    /// <summary>One sentence for the header of the card. A skipped step is never counted as a pass.</summary>
    public static string Summary(IReadOnlyList<SoloStep> steps, bool finished)
    {
        if (steps == null || steps.Count == 0)
        {
            return "Not run yet.";
        }
        int passed = steps.Count((SoloStep s) => s.State == SoloStepState.Passed);
        int failed = steps.Count((SoloStep s) => s.State == SoloStepState.Failed);
        int skipped = steps.Count((SoloStep s) => s.State == SoloStepState.Skipped);
        int open = steps.Count((SoloStep s) => s.State == SoloStepState.Waiting || s.State == SoloStepState.Running);
        if (failed > 0)
        {
            return failed + " of " + steps.Count + " checks failed. " + passed + " passed" + (skipped > 0 ? ", " + skipped + " skipped" : string.Empty) + ".";
        }
        if (open > 0)
        {
            return finished ? passed + " of " + steps.Count + " checks passed; " + open + " did not run." : passed + " of " + steps.Count + " checks passed so far.";
        }
        return skipped > 0 ? passed + " checks passed, " + skipped + " skipped." : "All " + passed + " checks passed.";
    }

    /// <summary>
    /// The live demo (the collaborator keeps moving an object) only starts when nothing has failed so far. A run that ends in failure tears the
    /// session down, so a demo begun before that would announce "Running" and vanish a moment later.
    /// </summary>
    public static bool DemoAllowed(IReadOnlyList<SoloStep> steps)
    {
        return steps != null && !steps.Any((SoloStep s) => s.State == SoloStepState.Failed);
    }

    public static bool AllPassed(IReadOnlyList<SoloStep> steps)
    {
        return steps != null && steps.Count > 0 && steps.All((SoloStep s) => s.State == SoloStepState.Passed);
    }
}

/// <summary>
/// The test collaborator's script, as data. A scripted collaborator joins the host session over the real transport, sends real presence,
/// and makes real edits to ONE object it creates itself, so it never touches the user's objects and, once it deletes that object, the scene
/// is exactly as it was. Everything here is engine-free so CoreTests can run the script against the real host authority.
/// </summary>
public static class SoloScript
{
    public const string ObjectName = "Solo Test Cube";

    public const double StopWaitSeconds = 6.0;

    /// <summary>How long the test waits for the readiness checks (Steam, publication, organization) to answer before it calls a blocked row real.</summary>
    public const double ReadinessGraceSeconds = 45.0;

    /// <summary>
    /// Whether the test may host yet. The checks answer asynchronously after the editor loads, so a blocked or missing row soon after start is
    /// waited on; the same row after the grace period is a real problem and is reported with its own reason.
    /// </summary>
    public static SoloHostReadiness HostReadiness(System.Collections.Generic.IReadOnlyList<ReadinessItem> items, double secondsWaited)
    {
        bool answered = items != null && items.Count > 0;
        if (answered && ReadinessRules.CanStart(items))
        {
            return SoloHostReadiness.Ready;
        }
        return secondsWaited >= ReadinessGraceSeconds ? SoloHostReadiness.Blocked : SoloHostReadiness.Waiting;
    }

    /// <summary>
    /// When the test may end its session. It must first remove its own object and have the host acknowledge that, because leaving a session
    /// checkpoints (saves) the scene and would otherwise write the still-present object into the file.
    /// </summary>
    public static SoloStopDecision StopDecision(bool objectPresent, bool editsPending, bool connected, double secondsWaited)
    {
        if (!connected || secondsWaited >= StopWaitSeconds)
        {
            return SoloStopDecision.Proceed;
        }
        return objectPresent || editsPending ? SoloStopDecision.Wait : SoloStopDecision.Proceed;
    }

    /// <summary>How many top-level objects a scene FILE holds; children nested under them are not counted. Null when the file cannot be read as a scene.</summary>
    public static int? TopLevelObjectCount(string sceneFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sceneFile) || !System.IO.File.Exists(sceneFile))
            {
                return null;
            }
            if (!(JsonNode.Parse(System.IO.File.ReadAllText(sceneFile)) is JsonObject root))
            {
                return null;
            }
            return root["GameObjects"] is JsonArray objects ? objects.Count : 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the editor has finished loading a scene, judged against what its file holds. Fewer objects than the file means it is still loading; the
    /// test must not begin then, because the host would publish a partial scene and a later save would write it back over the file.
    /// </summary>
    public static SoloSceneLoad SceneLoadState(int? expectedObjects, int loadedObjects)
    {
        if (!expectedObjects.HasValue)
        {
            return SoloSceneLoad.Unknown;
        }
        return loadedObjects >= expectedObjects.Value ? SoloSceneLoad.Loaded : SoloSceneLoad.Loading;
    }

    public static string SceneLoadDetail(string scene, int? expectedObjects, int loadedObjects)
    {
        if (!expectedObjects.HasValue)
        {
            return "The scene file for " + scene + " could not be read, so its size is unknown; continuing with " + loadedObjects + " object" + (loadedObjects == 1 ? string.Empty : "s") + " loaded.";
        }
        if (loadedObjects >= expectedObjects.Value)
        {
            return "Using " + scene + " (" + loadedObjects + " object" + (loadedObjects == 1 ? string.Empty : "s") + " loaded).";
        }
        return "Waiting for " + scene + " to finish loading (" + loadedObjects + " of " + expectedObjects.Value + " objects)…";
    }

    public const string BoxModel = "models/dev/box.vmdl";

    public static string Vec(double x, double y, double z)
    {
        return string.Join(",", new[] { x, y, z }.Select((double v) => v.ToString("0.######", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A position for SCENE STATE: whole units only. The engine stores floats in 32 bits, so a value like 12.4 comes back as 12.3999996 and the host
    /// editor answers with a corrective edit of its own, after which every edit built on the old text is refused as stale. Whole numbers are exact.
    /// </summary>
    public static string SnapVec(double x, double y, double z)
    {
        double S(double v) => Math.Round(v, MidpointRounding.AwayFromZero) + 0.0;
        return Vec(S(x), S(y), S(z));
    }

    public static string Quat(double x, double y, double z, double w)
    {
        return string.Join(",", new[] { x, y, z, w }.Select((double v) => v.ToString("0.#########", CultureInfo.InvariantCulture)));
    }

    public static string Color(double r, double g, double b, double a = 1.0)
    {
        return Quat(r, g, b, a);
    }

    /// <summary>The same shape the scene serializer writes for a plain box with one model renderer, which is what the host already accepts as a new object.</summary>
    public static ObjectState NewCube(Guid objectId, Guid rendererId, string position, string tint)
    {
        JsonObject data = new JsonObject
        {
            ["__guid"] = objectId.ToString(),
            ["__version"] = 2,
            ["Flags"] = 0,
            ["Name"] = ObjectName,
            ["Position"] = position,
            ["Rotation"] = "0,0,0,1",
            // 0.5 is exact in 32 bits; the 0.6 this used to write came back as 0.600000024 and made the host editor "correct" it.
            ["Scale"] = "0.5,0.5,0.5",
            ["Tags"] = string.Empty,
            ["Enabled"] = true,
            ["NetworkMode"] = 2,
            ["NetworkFlags"] = 0,
            ["NetworkOrphaned"] = 0,
            ["NetworkTransmit"] = true,
            ["OwnerTransfer"] = 1,
            ["Components"] = new JsonArray
            {
                new JsonObject
                {
                    // The serializer writes the type first; the host reads it to decide whether a new component is one it supports.
                    ["__type"] = "Sandbox.ModelRenderer",
                    ["__guid"] = rendererId.ToString(),
                    ["__enabled"] = true,
                    ["Flags"] = 0,
                    ["BodyGroups"] = 18446744073709551615UL,
                    ["CreateAttachments"] = false,
                    ["LodOverride"] = null,
                    ["MaterialGroup"] = null,
                    ["MaterialOverride"] = null,
                    ["Materials"] = null,
                    ["Model"] = BoxModel,
                    ["OnComponentDestroy"] = null,
                    ["OnComponentDisabled"] = null,
                    ["OnComponentEnabled"] = null,
                    ["OnComponentFixedUpdate"] = null,
                    ["OnComponentStart"] = null,
                    ["OnComponentUpdate"] = null,
                    ["RenderOptions"] = new JsonObject
                    {
                        ["GameLayer"] = true,
                        ["OverlayLayer"] = false,
                        ["BloomLayer"] = false,
                        ["AfterUILayer"] = false
                    },
                    ["RenderType"] = "On",
                    ["Tint"] = tint
                }
            }
        };
        return new ObjectState(objectId.ToString(), string.Empty, data);
    }

    private static ObjectState Clone(ObjectState state, Action<JsonObject> edit)
    {
        JsonObject data = (JsonObject)JsonNode.Parse(state.Data.ToJsonString());
        edit(data);
        return new ObjectState(state.Id, state.Parent, data);
    }

    public static ObjectState WithPosition(ObjectState state, string position)
    {
        return Clone(state, (JsonObject d) => d["Position"] = position);
    }

    public static ObjectState WithTint(ObjectState state, string tint)
    {
        return Clone(state, (JsonObject d) => ((JsonObject)((JsonArray)d["Components"])[0])["Tint"] = tint);
    }

    public static Change Create(ObjectState after)
    {
        return new Change(after.Id, null, after);
    }

    public static Change Update(ObjectState before, ObjectState after)
    {
        return new Change(before.Id, before, after);
    }

    public static Change Delete(ObjectState before)
    {
        return new Change(before.Id, before, null);
    }

    /// <summary>A point on a circle around <paramref name="center"/>, with a slow rise and fall so the marker reads as floating.</summary>
    public static (double X, double Y, double Z) Orbit((double X, double Y, double Z) center, double radius, double angleRadians, double bobHeight)
    {
        return (center.X + Math.Cos(angleRadians) * radius, center.Y + Math.Sin(angleRadians) * radius, center.Z + Math.Sin(angleRadians * 1.7) * bobHeight);
    }

    /// <summary>
    /// The rotation (x, y, z, w) that turns the +X axis, which is forward in this engine, toward <paramref name="target"/>. Yaw about +Z, then
    /// pitch about +Y, where a positive pitch looks down.
    /// </summary>
    public static (double X, double Y, double Z, double W) LookAt((double X, double Y, double Z) from, (double X, double Y, double Z) target)
    {
        double dx = target.X - from.X;
        double dy = target.Y - from.Y;
        double dz = target.Z - from.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-9)
        {
            return (0.0, 0.0, 0.0, 1.0);
        }
        double fz = dz / length;
        double yaw = Math.Atan2(dy, dx);
        double pitch = Math.Asin(Math.Clamp(-fz, -1.0, 1.0));
        (double X, double Y, double Z, double W) qz = (0.0, 0.0, Math.Sin(yaw / 2.0), Math.Cos(yaw / 2.0));
        (double X, double Y, double Z, double W) qy = (0.0, Math.Sin(pitch / 2.0), 0.0, Math.Cos(pitch / 2.0));
        return (
            qz.W * qy.X + qz.X * qy.W + qz.Y * qy.Z - qz.Z * qy.Y,
            qz.W * qy.Y - qz.X * qy.Z + qz.Y * qy.W + qz.Z * qy.X,
            qz.W * qy.Z + qz.X * qy.Y - qz.Y * qy.X + qz.Z * qy.W,
            qz.W * qy.W - qz.X * qy.X - qz.Y * qy.Y - qz.Z * qy.Z);
    }

    public static (double X, double Y, double Z) Rotate((double X, double Y, double Z, double W) q, (double X, double Y, double Z) v)
    {
        // v' = v + 2w(q x v) + 2 q x (q x v)
        double cx = q.Y * v.Z - q.Z * v.Y;
        double cy = q.Z * v.X - q.X * v.Z;
        double cz = q.X * v.Y - q.Y * v.X;
        double dx = q.Y * cz - q.Z * cy;
        double dy = q.Z * cx - q.X * cz;
        double dz = q.X * cy - q.Y * cx;
        return (v.X + 2.0 * (q.W * cx + dx), v.Y + 2.0 * (q.W * cy + dy), v.Z + 2.0 * (q.W * cz + dz));
    }

    /// <summary>The presence a real scene view would send: camera position and facing, a field of view, and a state word.</summary>
    public static JsonObject Presence((double X, double Y, double Z) position, (double X, double Y, double Z, double W) rotation, string state)
    {
        return new JsonObject
        {
            ["position"] = Vec(position.X, position.Y, position.Z),
            ["rotation"] = Quat(rotation.X, rotation.Y, rotation.Z, rotation.W),
            ["fov"] = 60.0,
            ["state"] = state,
            ["selection"] = new JsonArray()
        };
    }

    /// <summary>A tint that walks around the hue circle, so a property change is visible on screen rather than only in a log.</summary>
    public static string TintAt(double turns)
    {
        double h = (turns % 1.0 + 1.0) % 1.0 * 6.0;
        double x = 1.0 - Math.Abs(h % 2.0 - 1.0);
        (double r, double g, double b) = h < 1.0 ? (1.0, x, 0.0) : h < 2.0 ? (x, 1.0, 0.0) : h < 3.0 ? (0.0, 1.0, x) : h < 4.0 ? (0.0, x, 1.0) : h < 5.0 ? (x, 0.0, 1.0) : (1.0, 0.0, x);
        // Channels are snapped to multiples of 1/32, which a 32-bit float holds exactly and prints in at most five decimals, so the text the host
        // holds is the text the editor writes back.
        double Q(double v) => Math.Round((0.25 + 0.75 * v) * 32.0) / 32.0;
        return Color(Q(r), Q(g), Q(b));
    }

    /// <summary>Applies accepted changes to a local copy of the scene, the way the host's own state moves.</summary>
    public static void ApplyAccepted(Dictionary<string, ObjectState> known, IEnumerable<Change> changes)
    {
        foreach (Change change in changes ?? Enumerable.Empty<Change>())
        {
            if ((object)change.After == null)
            {
                known.Remove(change.Id);
            }
            else
            {
                known[change.Id] = change.After;
            }
        }
    }
}
