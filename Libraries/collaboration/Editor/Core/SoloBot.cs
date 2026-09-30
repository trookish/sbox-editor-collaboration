using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TeamCreate;

public enum SoloBotState
{
    Idle,
    Connecting,
    AwaitingWelcome,
    Live,
    Refused,
    Failed,
    Closed
}

/// <summary>What a collaborator says about itself in its hello. Everything except the identity fields is copied from the host.</summary>
public sealed class SoloHandshake
{
    public string Project;

    public string Baseline;

    public string Manifest;

    public string Org;

    public string LibraryVersion;

    public string SteamId;

    public string SteamToken;

    public string Name;

    /// <summary>The session token the host expects: the hex of the key derived from the invite.</summary>
    public string SessionToken;
}

/// <summary>
/// A scripted collaborator. It speaks the same wire protocol as a second editor (encrypted transport, hello, subscribe, edit, presence) but is
/// driven by code, so one person can watch a full session alone. It is polled from the frame tick and starts no threads. It lives in Core so
/// CoreTests can run it against the real host authority; nothing here can reach the editor's scene, only the wire.
/// </summary>
public sealed class SoloBot : IDisposable
{
    public const int MaxPacketsPerPump = 256;

    private Task<Wire> connecting;

    private Wire wire;

    private readonly Dictionary<string, Dictionary<string, ObjectState>> scenes = new Dictionary<string, Dictionary<string, ObjectState>>(StringComparer.Ordinal);

    private readonly HashSet<string> pendingEdits = new HashSet<string>(StringComparer.Ordinal);

    public string Peer { get; } = Guid.NewGuid().ToString();

    public SoloHandshake Handshake { get; }

    public SoloBotState State { get; private set; }

    /// <summary>The host's own words when it refused the handshake, or the transport's when the connection failed.</summary>
    public string Refusal { get; private set; }

    public string HostSession { get; private set; }

    public int AcceptedOwn { get; private set; }

    public int AcceptedFromOthers { get; private set; }

    public List<Packet> Rejects { get; } = new List<Packet>();

    public Dictionary<string, Packet> Others { get; } = new Dictionary<string, Packet>(StringComparer.Ordinal);

    public int PendingCount => pendingEdits.Count;

    public bool IsLive => State == SoloBotState.Live;

    public SoloBot(SoloHandshake handshake)
    {
        Handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
    }

    /// <summary>Starts a dial. The delegate is the route: a direct TCP connect, or the relay.</summary>
    public void Connect(Func<Task<Wire>> dial)
    {
        if (State != SoloBotState.Idle)
        {
            throw new InvalidOperationException("This collaborator has already connected once; make a new one.");
        }
        connecting = dial();
        State = SoloBotState.Connecting;
    }

    public IReadOnlyDictionary<string, ObjectState> Known(string scene)
    {
        return scenes.TryGetValue(scene, out Dictionary<string, ObjectState> state) ? state : new Dictionary<string, ObjectState>();
    }

    public bool HasScene(string scene)
    {
        return scenes.ContainsKey(scene);
    }

    public void Pump()
    {
        if (State == SoloBotState.Connecting && connecting != null && connecting.IsCompleted)
        {
            if (connecting.IsFaulted || connecting.IsCanceled)
            {
                State = SoloBotState.Failed;
                Refusal = (connecting.Exception?.GetBaseException().Message) ?? "The connection was cancelled.";
                return;
            }
            wire = connecting.Result;
            connecting = null;
            wire.Send(new Packet
            {
                Kind = "hello",
                ProtocolVersion = Protocol.CurrentVersion,
                Mode = CollaborationMode.Source,
                Peer = Peer,
                Project = Handshake.Project,
                Baseline = Handshake.Baseline,
                Name = Handshake.Name,
                Token = Handshake.SessionToken,
                SteamId = Handshake.SteamId,
                SteamToken = Handshake.SteamToken,
                OrgIdent = Handshake.Org,
                LibraryVersion = Handshake.LibraryVersion,
                ProjectManifest = Handshake.Manifest
            });
            State = SoloBotState.AwaitingWelcome;
        }
        if (wire == null)
        {
            return;
        }
        int budget = MaxPacketsPerPump;
        while (budget-- > 0 && wire.Incoming.TryDequeue(out Packet packet))
        {
            Handle(packet);
        }
        if (wire != null && wire.StoredFailure != null && (State == SoloBotState.Live || State == SoloBotState.AwaitingWelcome))
        {
            Refusal = wire.StoredFailure;
            State = State == SoloBotState.Live ? SoloBotState.Closed : SoloBotState.Failed;
        }
    }

    private void Handle(Packet p)
    {
        switch (p.Kind)
        {
            case "welcome":
                HostSession = p.Session;
                State = SoloBotState.Live;
                break;
            case "fatal":
            case "reject":
                if (State == SoloBotState.AwaitingWelcome)
                {
                    Refusal = p.Error ?? "The host refused this collaborator.";
                    State = SoloBotState.Refused;
                }
                else
                {
                    Rejects.Add(p);
                    if (p.Id != null)
                    {
                        pendingEdits.Remove(p.Id);
                    }
                }
                break;
            case "snapshot":
                if (p.Scene != null)
                {
                    scenes[p.Scene] = StateRules.Map(p.Objects ?? new List<ObjectState>());
                }
                break;
            case "accepted":
                if (p.Scene != null && scenes.TryGetValue(p.Scene, out Dictionary<string, ObjectState> known))
                {
                    SoloScript.ApplyAccepted(known, p.Changes);
                }
                if (p.Peer == Peer)
                {
                    AcceptedOwn++;
                    pendingEdits.Remove(p.Id);
                }
                else
                {
                    AcceptedFromOthers++;
                }
                break;
            case "presence":
                if (p.Peer != null && p.Peer != Peer)
                {
                    Others[p.Peer] = p;
                }
                break;
            case "left":
                if (p.Peer != null)
                {
                    Others.Remove(p.Peer);
                }
                break;
        }
    }

    /// <summary>
    /// Subscribes to a scene the host already holds; it answers with its own state. Only the first subscriber to a scene ever seeds it, which is
    /// what the host editor does, and what a test uses to stand in for one.
    /// </summary>
    public void Subscribe(string scene, List<ObjectState> seed = null)
    {
        Require();
        wire.Send(new Packet { Kind = "subscribe", Scene = scene, Objects = seed ?? new List<ObjectState>() });
    }

    /// <summary>Sends one edit built against the state this collaborator last saw. Returns the edit's id, or null when the bot is not live.</summary>
    public string SendEdit(string scene, IEnumerable<Change> changes, bool final = true)
    {
        Require();
        Packet edit = new Packet { Kind = "edit", Scene = scene, Peer = Peer, Changes = changes.ToList(), Final = final };
        pendingEdits.Add(edit.Id);
        wire.Send(edit);
        return edit.Id;
    }

    public void SendPresence(string scene, JsonObject presence)
    {
        Require();
        wire.Send(new Packet { Kind = "presence", Scene = scene, Peer = Peer, Presence = presence });
        wire.Send(new Packet { Kind = "ping" });
    }

    private void Require()
    {
        if (wire == null || State != SoloBotState.Live && State != SoloBotState.AwaitingWelcome)
        {
            throw new InvalidOperationException("The test collaborator is not connected.");
        }
    }

    /// <summary>Says goodbye the way an editor does, so the host clears the roster row at once instead of after its timeout.</summary>
    public void Leave()
    {
        if (wire != null && State == SoloBotState.Live)
        {
            try
            {
                wire.Send(new Packet { Kind = "leave", Peer = Peer });
            }
            catch (Exception)
            {
            }
        }
        Dispose();
    }

    public void Dispose()
    {
        wire?.Dispose();
        wire = null;
        if (State == SoloBotState.Live || State == SoloBotState.AwaitingWelcome || State == SoloBotState.Connecting)
        {
            State = SoloBotState.Closed;
        }
    }
}
