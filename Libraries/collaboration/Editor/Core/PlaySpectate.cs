using System;
using System.Collections.Generic;

namespace TeamCreate;

public enum PlaySpectateDecision
{
    /// <summary>Synchronize as usual.</summary>
    Normal,

    /// <summary>
    /// The local editor is playing a copy of the scene. Source edits stop being sampled and incoming scene changes are
    /// held, but the connection, the roster and presence carry on, so the people still in the editor can watch.
    /// </summary>
    PausedForPlay,

    /// <summary>No supported saved scene is in front of the user. Nothing to sample or apply; the connection stays up.</summary>
    NoScene,

    /// <summary>Something that needs a person: a hotload during play, or a state no rule anticipated.</summary>
    FailStop
}

public static class PlaySpectateRules
{
    /// <summary>
    /// Decides what a frame does with source synchronization. Before this rule, pressing Play ended the session
    /// with "Stop play mode..." and even closing the last scene tab did too; a player could not keep a collaborator
    /// in the loop, and a collaborator could not watch. The one state that still stops everything is a hotload that
    /// interrupted play, because that path archives recovery data and must be ended with Leave.
    /// </summary>
    public static PlaySpectateDecision Decide(bool localPlaying, PlayModeLifecycleState lifecycle, bool sourcePacketsAdmissible, bool hasSupportedScene)
    {
        if (lifecycle == PlayModeLifecycleState.RuntimeBlocked)
        {
            return PlaySpectateDecision.FailStop;
        }
        if (localPlaying)
        {
            // An approved experimental runtime run keeps its existing, stricter path.
            return sourcePacketsAdmissible ? PlaySpectateDecision.Normal : PlaySpectateDecision.PausedForPlay;
        }
        if (lifecycle == PlayModeLifecycleState.RuntimeStarting)
        {
            return PlaySpectateDecision.PausedForPlay;
        }
        if (!hasSupportedScene)
        {
            return PlaySpectateDecision.NoScene;
        }
        return sourcePacketsAdmissible ? PlaySpectateDecision.Normal : PlaySpectateDecision.FailStop;
    }

    /// <summary>The state word other editors see next to a collaborator's name.</summary>
    public static string PresenceState(bool saving, bool localPlaying, bool editing)
    {
        if (saving)
        {
            return "saving";
        }
        if (localPlaying)
        {
            return "playing";
        }
        return editing ? "editing" : "idle";
    }

    public static string StatusText(string state)
    {
        switch (state)
        {
            case "playing":
                return "Playing the game";
            case "editing":
                return "Editing";
            case "saving":
                return "Saving";
            default:
                return "Idle";
        }
    }
}

/// <summary>
/// Scene packets received while the local editor is playing. The host does not know a peer is paused, so packets
/// keep arriving; leaving them in the wire's queue would trip its 256-packet cap and drop the connection.
/// They are moved here, in order, and applied when play stops.
/// </summary>
public sealed class HeldPacketBuffer
{
    public const int Capacity = 4096;

    private readonly Queue<Packet> held = new Queue<Packet>();

    public int Count => held.Count;

    public bool IsEmpty => held.Count == 0;

    /// <summary>False when the buffer is full. The caller must then stop synchronizing, because dropping a packet would desynchronize silently.</summary>
    public bool TryHold(Packet packet)
    {
        if (packet == null)
        {
            return true;
        }
        if (held.Count >= Capacity)
        {
            return false;
        }
        held.Enqueue(packet);
        return true;
    }

    public bool TryTake(out Packet packet)
    {
        return held.TryDequeue(out packet);
    }

    public void Clear()
    {
        held.Clear();
    }

    /// <summary>Whether a packet kind changes scene state and therefore has to wait for play to end.</summary>
    public static bool IsSceneChange(string kind)
    {
        return kind == "snapshot" || kind == "accepted";
    }
}
