using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

public sealed partial class SessionServer : IDisposable
{
    private sealed class Member
    {
        public Wire Wire;

        public string Peer;

        public string Name;

        public string SteamId;

        public Packet Presence;

        public DateTime LastSeen = DateTime.UtcNow;

        public readonly DateTime Accepted = DateTime.UtcNow;

        // Why a send to this member failed (its queue is full or its connection closed), or null. The member is removed at the next safe point
        // of the pump, never in the middle of a loop over the members, and nothing more is sent to it meanwhile.
        public string Dropped;

        public int ProtocolErrors;
    }

    private sealed class Refusal
    {
        public Wire Wire;

        public int Generation;
    }

    private sealed class PendingVerification
    {
        public Member Member;

        public Packet Hello;

        public Task<SteamTokenVerdict> Task;
    }

    private readonly Listener listener;

    private readonly List<Member> members = new List<Member>();

    private readonly string project;

    private readonly string baseline;

    private readonly string token;

    private readonly HashSet<string> compatibleBaselines = new HashSet<string>();

    private readonly string sessionId = Guid.NewGuid().ToString();

    private readonly Authority authority;

    private readonly Func<Packet, SteamTokenVerdict> verifyVerdict;

    private readonly List<Refusal> refusals = new List<Refusal>();

    private int pumpGeneration;

    private readonly FileStream journal;

    public const int MaxPendingVerifications = 8;

    /// <summary>The refusal of a hello while the host is already verifying <see cref="MaxPendingVerifications"/> others. It passes.</summary>
    public const string VerificationBusyError = "Too many peers are awaiting identity verification";

    /// <summary>Messages from one member the host could not process before it closes that member's connection. A correct editor sends none.</summary>
    public const int MaxProtocolErrors = 16;

    /// <summary>How long a connection may stay without saying hello. An unproven connection holds a slot, so it is not allowed to hold it long.</summary>
    public static readonly TimeSpan HandshakeLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long a member may stay silent before it is treated as gone. Members ping every two seconds. Tests shorten it.</summary>
    public TimeSpan SilenceLimit { get; set; } = TimeSpan.FromSeconds(15);

    private readonly List<PendingVerification> pendingVerifications = new List<PendingVerification>();

    private readonly Func<Packet, Task<SteamTokenVerdict>> asyncVerifier;

    private readonly CancellationTokenSource verificationLifetime = new CancellationTokenSource();

    private readonly DriftDetector drift = new DriftDetector();

    // The host's digest of each scene, valid for one revision: every edit moves the revision, and a scene changes only through an edit,
    // so a ping every two seconds from each member costs one hash per change instead of one per ping.
    private readonly Dictionary<string, (long Revision, string Digest)> hostDigests = new Dictionary<string, (long Revision, string Digest)>();

    private readonly System.Diagnostics.Stopwatch driftClock = System.Diagnostics.Stopwatch.StartNew();

    private string driftNoticePeer;

    public string Failure { get; private set; }

    /// <summary>
    /// A formal sentence for the host's user when a guest's editor keeps drifting from the host's scene although it was resynchronised
    /// repeatedly, or null. It clears when that guest agrees with the host again or leaves.
    /// </summary>
    public string DriftNotice { get; private set; }

    /// <summary>Seconds on a monotonic clock for the drift rules; tests replace it to control time.</summary>
    public Func<double> DriftClock { get; set; }

    /// <summary>How many times the host has hashed a scene for drift checks (cache misses), for tests of the per-revision cache.</summary>
    public int HostDigestComputations { get; private set; }

    public string StoredFailure => (Failure == null) ? null : CollaborationTransportDiagnostics.BoundedStoredDiagnostic(Failure);

    public bool AllowUnverifiedPeers { get; set; }

    public Func<Packet, PeerFacts> peerFacts { get; set; }

    public string hostLibraryVersion { get; set; }

    public string hostManifest { get; set; }

    public CollaborationConfig config { get; set; }

    /// <summary>
    /// Guests that arrive through the relay instead of the TCP listener. They are drained into the same member
    /// list under the same connection cap, and admitted by the same hello, gates and verification, so a relayed
    /// peer is held to exactly the rules a direct one is.
    /// </summary>
    public RelayHost Relay { get; set; }

    public int PendingVerificationCount => pendingVerifications.Count;

    public SessionServer(string address, int port, string project, string baseline, string token, string journalPath, Func<Packet, SteamTokenVerdict> verifyVerdict = null, Func<Packet, Task<SteamTokenVerdict>> asyncVerifyVerdict = null, byte[] sessionKey = null)
    {
        this.project = project;
        this.baseline = baseline;
        this.token = token;
        this.verifyVerdict = verifyVerdict;
        asyncVerifier = asyncVerifyVerdict;
        compatibleBaselines.Add(baseline);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
        journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        authority = new Authority((Packet p) =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p) + "\n");
            journal.Write(bytes);
            journal.Flush(flushToDisk: true);
        });
        try
        {
            listener = new Listener(address, port, sessionKey);
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    public void Pump()
    {
        if (listener.Failure != null)
        {
            throw new IOException(listener.StoredFailure);
        }
        pumpGeneration++;
        CloseExpiredRefusals();
        PollVerifications();
        Wire result;
        while (listener.Accepted.TryDequeue(out result))
        {
            if (members.Count >= SessionLimits.MaxConnections)
            {
                result.Dispose();
                continue;
            }
            members.Add(new Member
            {
                Wire = result
            });
        }
        Wire relayed;
        while (Relay != null && Relay.Accepted.TryDequeue(out relayed))
        {
            if (members.Count >= SessionLimits.MaxConnections)
            {
                relayed.Dispose();
                continue;
            }
            members.Add(new Member
            {
                Wire = relayed
            });
        }
        Member[] array = members.ToArray();
        foreach (Member m in array)
        {
            if (!members.Contains(m))
            {
                continue;
            }
            if (m.Dropped != null || m.Wire.StoredFailure != null || IsSilent(m))
            {
                Remove(m);
                continue;
            }
            int num = 64;
            while (true)
            {
                // A member removed or dropped by an earlier packet (its own leave, a replacement, a failed send) is not served further.
                if (num-- <= 0 || m.Dropped != null || !members.Contains(m) || !NextPacket(m, out var p))
                {
                    break;
                }
                m.LastSeen = DateTime.UtcNow;
                try
                {
                    string text;
                    if (m.Peer == null)
                    {
                        if (pendingVerifications.Any((PendingVerification v) => v.Member == m))
                        {
                            // The same editor asking again while its first hello is being verified: that answer will serve both.
                            continue;
                        }
                        text = Protocol.GetHandshakeError(p, requireValidPeer: true);
                        if (text == null && p.Token != token)
                        {
                            text = "Incorrect session key";
                        }
                        if (text == null && p.Project != project)
                        {
                            text = "Project ID differs. Use the same committed ProjectSettings/collaboration.json.";
                        }
                        if (text == null)
                        {
                            // A peer whose files differ is admitted and synchronized after welcome; only its engine build must match.
                            text = AdmissionRules.BaselineRefusal(peerManifest: p.ProjectManifest, hostManifest: hostManifest, peerBaseline: p.Baseline,
                                compatibleBaselines: compatibleBaselines, peerEngine: p.EngineBaseline, hostEngine: HostEngineBaseline);
                        }
                        if (text == null && (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64))
                        {
                            text = "Invalid collaborator name";
                        }
                        if (text != null)
                        {
                            RejectWith(m, "fatal", text);
                            continue;
                        }
                        // Whether the account is already here is decided after verification: a proven account that reconnects replaces its
                        // own old connection, so the check cannot come first.
                        if (asyncVerifier != null)
                        {
                            if (!TryDeferVerification(m, p))
                            {
                                RejectWith(m, "fatal", VerificationBusyError);
                            }
                            continue;
                        }
                        Admit(m, p, VerifyVerdict(p), "reject");
                        continue;
                    }
                    p.Peer = m.Peer;
                    if (HandleFilePacket(m, p)) continue;
                    if (HandlePlayPacket(m, p)) continue;
                    if (HandleTransientPacket(m, p)) continue;
                    switch (p.Kind)
                    {
                    case "subscribe":
                        if (string.IsNullOrWhiteSpace(p.Scene) || !p.Scene.StartsWith("scenes/", StringComparison.Ordinal) || p.Scene.Contains(".."))
                        {
                            throw new InvalidOperationException("Scene must be under Assets/scenes");
                        }
                        SendTo(m, authority.Subscribe(p));
                        break;
                    case "edit":
                    {
                        Packet packet = authority.Apply(p);
                        if (packet.Kind == "reject")
                        {
                            SendTo(m, packet);
                        }
                        else
                        {
                            Broadcast(packet);
                        }
                        break;
                    }
                    case SettingsMerge.PacketKind:
                    {
                        // Routed exactly like an edit: the merged settings go to every member, a refusal only to the sender.
                        Packet packet = authority.ApplySettings(p);
                        if (packet.Kind == "reject")
                        {
                            SendTo(m, packet);
                        }
                        else
                        {
                            Broadcast(packet);
                        }
                        break;
                    }
                    case "presence":
                        if (!double.IsFinite(p.SentMs))
                        {
                            throw new InvalidOperationException("The presence update carried a time that is not a finite number.");
                        }
                        p.Name = m.Name;
                        // The account the host admitted, never whatever the sender wrote: this is what every editor looks the profile
                        // picture up by, so a guest must not be able to show someone else's.
                        p.SteamId = m.SteamId;
                        // Relayed before it is kept: a presence that cannot be relayed fails here, for its sender alone, and is never
                        // handed to the people who join later.
                        Broadcast(p);
                        m.Presence = p;
                        break;
                    case "ping":
                        SendTo(m, new Packet
                        {
                            Kind = "pong"
                        });
                        CheckDrift(m, p);
                        break;
                    case "leave":
                        Remove(m);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown message type");
                    }
                }
                catch (Exception error)
                {
                    // The packet is at fault, so its sender alone hears about it. Every send here is guarded: a failure to tell the sender
                    // must not escape the pump, which would end the session for everybody.
                    if (m.Dropped != null || !members.Contains(m))
                    {
                        break;
                    }
                    if (++m.ProtocolErrors >= MaxProtocolErrors)
                    {
                        RejectWith(m, "fatal", "The host closed this connection because it kept sending messages the host could not process.");
                        break;
                    }
                    SendTo(m, new Packet
                    {
                        Kind = "fatal",
                        Error = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Host processing failed: ", error)
                    });
                }
            }
        }
        PumpFileOutboxes();
        PumpBlobs();
        RemoveDropped();
    }

    // Silence is measured from the last frame the wire read, not the last one the pump handled. A connection that has not said hello is
    // held to the shorter handshake limit, counted from when it arrived, unless a hello is already waiting or being verified.
    private bool IsSilent(Member m)
    {
        DateTime now = DateTime.UtcNow;
        DateTime heard = m.Wire.LastReceivedUtc > m.LastSeen ? m.Wire.LastReceivedUtc : m.LastSeen;
        if (now - heard > SilenceLimit)
        {
            return true;
        }
        return m.Peer == null && now - m.Accepted > HandshakeLimit && m.Wire.Incoming.IsEmpty && !pendingVerifications.Any((PendingVerification v) => v.Member == m);
    }

    /// <summary>
    /// Sends to one member and reports whether it was queued. A full queue or a closed connection marks that member dropped; it is removed
    /// at the next safe point and nobody else is affected. A packet that cannot be written at all is the packet's fault, not the member's,
    /// so that exception is left to reach the handler of whoever sent it.
    /// </summary>
    private bool SendTo(Member target, Packet packet)
    {
        if (target.Dropped != null)
        {
            return false;
        }
        try
        {
            target.Wire.Send(packet);
            return true;
        }
        catch (FrameTooLargeException)
        {
            throw;
        }
        catch (IOException error)
        {
            target.Dropped = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Send failed: ", error);
            return false;
        }
        catch (ObjectDisposedException error)
        {
            target.Dropped = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Send failed: ", error);
            return false;
        }
    }

    private void RemoveDropped()
    {
        foreach (Member member in members.Where((Member x) => x.Dropped != null).ToArray())
        {
            Remove(member);
        }
    }

    // One admission path for both verification modes. A refusal of the identity uses refusalKind (the synchronous path has always answered
    // "reject", the deferred one "fatal"); a refusal by the project's admission rules or the participant cap is always "reject".
    private void Admit(Member m, Packet hello, SteamTokenVerdict verdict, string refusalKind)
    {
        string text = SteamIdentityAdmission.GetVerificationError(verdict, AllowUnverifiedPeers);
        List<Member> replaced = null;
        if (text == null)
        {
            text = IdentityConflict(m, hello, verdict == SteamTokenVerdict.Verified, out replaced);
        }
        if (text != null)
        {
            RejectWith(m, refusalKind, text);
            return;
        }
        text = AdmissionGateError(hello) ?? ParticipantCapError(m, replaced);
        if (text != null)
        {
            Reject(m, text);
            return;
        }
        foreach (Member old in replaced)
        {
            Remove(old);
        }
        m.Peer = hello.Peer;
        m.Name = hello.Name;
        m.SteamId = hello.SteamId;
        if (!SendTo(m, Welcome()))
        {
            return;
        }
        foreach (Member item in members.Where((Member x) => x.Presence != null).ToArray())
        {
            SendTo(m, item.Presence);
        }
    }

    /// <summary>
    /// Which members a joining editor replaces, or why it may not join. A collaborator ID belongs to the account that first used it: the same
    /// account under the same ID is the same editor reconnecting and replaces its old connection, another account claiming it is refused. A
    /// proven account under a new ID is the same person whose editor restarted, so its old connection is replaced at once instead of being
    /// refused until it times out; an unproven one cannot show that and is refused as a duplicate.
    /// </summary>
    private string IdentityConflict(Member joining, Packet hello, bool proven, out List<Member> replaced)
    {
        replaced = members.Where((Member x) => x != joining && x.Peer != null && x.Peer == hello.Peer).ToList();
        if (replaced.Any((Member x) => !SameAccount(x.SteamId, hello.SteamId)))
        {
            return "That collaborator ID belongs to another account in this session.";
        }
        if (proven)
        {
            List<Member> previous = replaced;
            replaced.AddRange(members.Where((Member x) => x != joining && x.Peer != null && !previous.Contains(x) && SameAccount(x.SteamId, hello.SteamId)).ToList());
        }
        List<Member> leaving = replaced;
        return SteamIdentityAdmission.GetDuplicateError(hello.SteamId, from x in members
            where x != joining && x.SteamId != null && !leaving.Contains(x)
            select x.SteamId);
    }

    private static bool SameAccount(string a, string b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }
        if (SteamIdentityRules.TryParse(a, out var left) && SteamIdentityRules.TryParse(b, out var right))
        {
            return string.Equals(SteamIdentityRules.Canonical(left), SteamIdentityRules.Canonical(right), StringComparison.Ordinal);
        }
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    // A ping from a peer that predates the digest, or one that names a scene the host does not hold, is answered with the pong alone. Nothing
    // here may fail the member's session: drift detection is a courtesy on top of the heartbeat.
    private void CheckDrift(Member member, Packet ping)
    {
        if (string.IsNullOrEmpty(ping.Digest) || !ping.DigestRevision.HasValue || string.IsNullOrEmpty(ping.DigestScene) || !authority.Scenes.ContainsKey(ping.DigestScene))
        {
            return;
        }
        string hostDigest = HostDigestOf(ping.DigestScene);
        if (hostDigest == null)
        {
            return;
        }
        double now = (DriftClock ?? (() => driftClock.Elapsed.TotalSeconds))();
        DriftVerdict verdict = drift.ShouldResync(member.Peer, ping.DigestScene, ping.DigestRevision.Value, ping.Digest, authority.Revision, hostDigest, now);
        if (ping.DigestRevision.Value == authority.Revision && string.Equals(ping.Digest, hostDigest, StringComparison.Ordinal) && driftNoticePeer == member.Peer)
        {
            ClearDriftNotice();
        }
        if (verdict == DriftVerdict.Resync)
        {
            // The same packet a subscribe is answered with: the scene already exists, so this only reads the host's state.
            SendTo(member, authority.Subscribe(new Packet
            {
                Scene = ping.DigestScene
            }));
        }
        else if (verdict == DriftVerdict.Persistent)
        {
            driftNoticePeer = member.Peer;
            DriftNotice = "The editor of " + member.Name + " keeps drifting from the host's copy of \"" + ping.DigestScene + "\" although it was resynchronised " + DriftDetector.PersistentResyncCount + " times. Ask them to leave and rejoin the session.";
        }
    }

    private string HostDigestOf(string scene)
    {
        if (hostDigests.TryGetValue(scene, out var cached) && cached.Revision == authority.Revision)
        {
            return cached.Digest;
        }
        try
        {
            HostDigestComputations++;
            // Settings are part of the scene's state, so they are part of its fingerprint: a guest that agrees on every object but holds
            // other settings has drifted too. Each settings change moves the revision, so the per-revision cache stays valid.
            string digest = SceneDigest.Compute(authority.Scenes[scene], authority.SceneSettings.GetValueOrDefault(scene));
            hostDigests[scene] = (authority.Revision, digest);
            return digest;
        }
        catch (ArgumentException)
        {
            // The digest refuses a scene it cannot fingerprint faithfully. That says nothing about the guest, so no comparison is made.
            return null;
        }
    }

    private void ClearDriftNotice()
    {
        DriftNotice = null;
        driftNoticePeer = null;
    }

    private void Reject(Member member, string reason)
    {
        RejectWith(member, "reject", reason);
    }

    // Counted at the moment of admission, not on arrival: an unproven socket is not a participant, and a guest reconnecting under the same peer id
    // replaces its old connection (removed right after this check), so it is not counted twice. The host is one of the participants.
    private string ParticipantCapError(Member joining, List<Member> replaced)
    {
        int others = members.Count((Member x) => x != joining && x.Peer != null && !replaced.Contains(x));
        return SessionLimits.AdmitGuest(others);
    }

    private string AdmissionGateError(Packet hello)
    {
        if (this.peerFacts == null)
        {
            return null;
        }
        PeerFacts peerFacts;
        try
        {
            peerFacts = this.peerFacts(hello);
        }
        catch (Exception error)
        {
            return "Admission requirements could not be evaluated for this peer: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error);
        }
        PeerFacts peerFacts2 = peerFacts ?? new PeerFacts(hello.SteamId, IdentityVerified: false, IsFriend: false, IsOrgMember: false, hello.LibraryVersion, hello.ProjectManifest, hello.OrgIdent);
        peerFacts2 = peerFacts2 with
        {
            SteamId = hello.SteamId,
            IdentityVerified = true
        };
        string failedGate;
        return AdmissionRules.Evaluate(peerFacts2, config, hostLibraryVersion, hostManifest, out failedGate, hello.Name);
    }

    private bool TryDeferVerification(Member member, Packet hello)
    {
        if (pendingVerifications.Any((PendingVerification v) => v.Member == member))
        {
            return false;
        }
        if (pendingVerifications.Count >= MaxPendingVerifications)
        {
            return false;
        }
        Task<SteamTokenVerdict> task = asyncVerifier(hello);
        if (task == null)
        {
            return false;
        }
        pendingVerifications.Add(new PendingVerification
        {
            Member = member,
            Hello = hello,
            Task = task
        });
        return true;
    }

    private void PollVerifications()
    {
        if (pendingVerifications.Count == 0)
        {
            return;
        }
        for (int num = pendingVerifications.Count - 1; num >= 0; num--)
        {
            PendingVerification pending = pendingVerifications[num];
            if (pending.Task.IsCompleted)
            {
                pendingVerifications.RemoveAt(num);
                // A guest that left while it was being checked is forgotten: its verdict has nobody to answer.
                if (members.Contains(pending.Member) && pending.Member.Dropped == null)
                {
                    SteamTokenVerdict verdict;
                    try
                    {
                        verdict = pending.Task.GetAwaiter().GetResult();
                    }
                    catch (Exception)
                    {
                        verdict = SteamTokenVerdict.ServiceUnavailable;
                    }
                    Admit(pending.Member, pending.Hello, verdict, "fatal");
                }
            }
        }
    }

    private void CancelVerifications()
    {
        try
        {
            verificationLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (PendingVerification pendingVerification in pendingVerifications)
        {
            Remove(pendingVerification.Member);
        }
        pendingVerifications.Clear();
    }

    private void RejectWith(Member member, string kind, string reason)
    {
        try
        {
            member.Wire.Send(new Packet
            {
                Kind = kind,
                Error = reason
            });
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        members.Remove(member);
        pendingVerifications.RemoveAll((PendingVerification v) => v.Member == member);
        refusals.Add(new Refusal
        {
            Wire = member.Wire,
            Generation = pumpGeneration
        });
    }

    private void CloseExpiredRefusals()
    {
        Refusal[] array = refusals.Where((Refusal x) => x.Generation < pumpGeneration).ToArray();
        foreach (Refusal refusal in array)
        {
            refusals.Remove(refusal);
            try
            {
                refusal.Wire.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private SteamTokenVerdict VerifyVerdict(Packet p)
    {
        return verifyVerdict?.Invoke(p) ?? SteamTokenVerdict.ServiceUnavailable;
    }

    /// <summary>
    /// Sends one packet to every admitted member. A recipient whose queue is full or whose connection closed is dropped on its own (see
    /// <see cref="SendTo"/>); a packet that cannot be written at all fails on the first recipient and is thrown before anyone else is
    /// tried, so it is refused once, for its sender, instead of costing every recipient the session.
    /// </summary>
    private void Broadcast(Packet packet)
    {
        foreach (Member member in members.Where((Member x) => x.Peer != null && x.Dropped == null).ToArray())
        {
            SendTo(member, packet);
        }
    }

    // Features are advertised only when this host can actually serve them: without a project root there are no files to offer.
    private Packet Welcome()
    {
        // File and geometry features come from WelcomeFeatures (SessionServer.Blobs.cs).
        var features = WelcomeFeatures() ?? new List<string>();
        features.Add(TransientLimits.Feature);
        if (SharedPlayEnabled)
        {
            features.Add(PlayRelayPolicy.Feature);
        }
        return new Packet
        {
            Kind = "welcome",
            Session = sessionId,
            Features = features.Count == 0 ? null : features
        };
    }

    public void AllowSavedBaseline(string fingerprint)
    {
        compatibleBaselines.Add(fingerprint);
    }

    private void Remove(Member member)
    {
        if (members.Remove(member))
        {
            ForgetFileMember(member);
            ForgetBlobMember(member);
            ForgetPlayMember(member);
            pendingVerifications.RemoveAll((PendingVerification v) => v.Member == member);
            member.Wire.Dispose();
            if (member.Peer != null)
            {
                drift.Forget(member.Peer);
                if (driftNoticePeer == member.Peer)
                {
                    ClearDriftNotice();
                }
                Broadcast(new Packet
                {
                    Kind = "left",
                    Peer = member.Peer
                });
            }
        }
    }

    public void Dispose()
    {
        CancelVerifications();
        verificationLifetime.Dispose();
        listener.Dispose();
        foreach (Refusal refusal in refusals)
        {
            try
            {
                refusal.Wire.Dispose();
            }
            catch (Exception)
            {
            }
        }
        refusals.Clear();
        foreach (Member member in members)
        {
            member.Wire.Dispose();
        }
        members.Clear();
        journal.Dispose();
    }
}
