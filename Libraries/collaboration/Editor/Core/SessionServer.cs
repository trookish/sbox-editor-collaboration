using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

public sealed class SessionServer : IDisposable
{
    private sealed class Member
    {
        public Wire Wire;

        public string Peer;

        public string Name;

        public string SteamId;

        public Packet Presence;

        public DateTime LastSeen = DateTime.UtcNow;
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
            if (members.Count >= 16)
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
            if (members.Count >= 16)
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
            if (m.Wire.StoredFailure != null || DateTime.UtcNow - m.LastSeen > TimeSpan.FromSeconds(15L))
            {
                Remove(m);
                continue;
            }
            int num = 64;
            while (true)
            {
                if (num-- <= 0 || !m.Wire.Incoming.TryDequeue(out var p))
                {
                    break;
                }
                m.LastSeen = DateTime.UtcNow;
                try
                {
                    string text;
                    if (m.Peer == null)
                    {
                        text = Protocol.GetHandshakeError(p, requireValidPeer: true);
                        if (text == null && p.Token != token)
                        {
                            text = "Incorrect session key";
                        }
                        if (text == null && p.Project != project)
                        {
                            text = "Project ID differs. Use the same committed ProjectSettings/collaboration.json.";
                        }
                        if (text == null && !compatibleBaselines.Contains(p.Baseline))
                        {
                            text = "Project/scene baseline differs (engine, source, assets or metadata). Sync copies in Git before joining.";
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
                        text = SteamIdentityAdmission.GetDuplicateError(p.SteamId, RosterSteamIds(m));
                        if (text != null)
                        {
                            goto IL_0335;
                        }
                        if (asyncVerifier != null)
                        {
                            if (!TryDeferVerification(m, p))
                            {
                                RejectWith(m, "fatal", "Too many peers are awaiting identity verification");
                            }
                            continue;
                        }
                        text = SteamIdentityAdmission.GetVerificationError(VerifyVerdict(p), AllowUnverifiedPeers);
                        goto IL_0335;
                    }
                    p.Peer = m.Peer;
                    switch (p.Kind)
                    {
                    case "subscribe":
                        if (string.IsNullOrWhiteSpace(p.Scene) || !p.Scene.StartsWith("scenes/", StringComparison.Ordinal) || p.Scene.Contains(".."))
                        {
                            throw new InvalidOperationException("Scene must be under Assets/scenes");
                        }
                        m.Wire.Send(authority.Subscribe(p));
                        break;
                    case "edit":
                    {
                        Packet packet = authority.Apply(p);
                        if (packet.Kind == "reject")
                        {
                            m.Wire.Send(packet);
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
                            m.Wire.Send(packet);
                        }
                        else
                        {
                            Broadcast(packet);
                        }
                        break;
                    }
                    case "presence":
                        p.Name = m.Name;
                        // The account the host admitted, never whatever the sender wrote: this is what every editor looks the profile
                        // picture up by, so a guest must not be able to show someone else's.
                        p.SteamId = m.SteamId;
                        m.Presence = p;
                        Broadcast(p);
                        break;
                    case "ping":
                        m.Wire.Send(new Packet
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
                    goto end_IL_0174;
                    IL_0335:
                    if (text != null)
                    {
                        Reject(m, text);
                        continue;
                    }
                    text = AdmissionGateError(p);
                    if (text != null)
                    {
                        Reject(m, text);
                        continue;
                    }
                    Member[] array2 = members.Where((Member x) => x != m && x.Peer == p.Peer).ToArray();
                    foreach (Member member in array2)
                    {
                        Remove(member);
                    }
                    m.Peer = p.Peer;
                    m.Name = p.Name;
                    m.SteamId = p.SteamId;
                    m.Wire.Send(new Packet
                    {
                        Kind = "welcome",
                        Session = sessionId
                    });
                    foreach (Member item in members.Where((Member x) => x.Presence != null))
                    {
                        m.Wire.Send(item.Presence);
                    }
                    end_IL_0174:;
                }
                catch (Exception error)
                {
                    m.Wire.Send(new Packet
                    {
                        Kind = "fatal",
                        Error = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Host processing failed: ", error)
                    });
                }
            }
        }
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
            member.Wire.Send(authority.Subscribe(new Packet
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
        if (pendingVerifications.Count >= 8)
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
                if (pending.Member.Wire != null)
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
                    string verificationError = SteamIdentityAdmission.GetVerificationError(verdict, AllowUnverifiedPeers);
                    if (verificationError != null)
                    {
                        RejectWith(pending.Member, "fatal", verificationError);
                    }
                    else
                    {
                        string duplicateError = SteamIdentityAdmission.GetDuplicateError(pending.Hello.SteamId, RosterSteamIds(pending.Member));
                        if (duplicateError != null)
                        {
                            RejectWith(pending.Member, "fatal", duplicateError);
                        }
                        else
                        {
                            string text = AdmissionGateError(pending.Hello);
                            if (text != null)
                            {
                                Reject(pending.Member, text);
                            }
                            else
                            {
                                Member m = pending.Member;
                                Member[] array = members.Where((Member x) => x != m && x.Peer == pending.Hello.Peer).ToArray();
                                foreach (Member member in array)
                                {
                                    Remove(member);
                                }
                                m.Peer = pending.Hello.Peer;
                                m.Name = pending.Hello.Name;
                                m.SteamId = pending.Hello.SteamId;
                                m.Wire.Send(new Packet
                                {
                                    Kind = "welcome",
                                    Session = sessionId
                                });
                                foreach (Member item in members.Where((Member x) => x.Presence != null))
                                {
                                    m.Wire.Send(item.Presence);
                                }
                            }
                        }
                    }
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
        members.Remove(member);
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

    private IEnumerable<string> RosterSteamIds(Member joining)
    {
        return from x in members
            where x != joining && x.SteamId != null
            select x.SteamId;
    }

    private SteamTokenVerdict VerifyVerdict(Packet p)
    {
        return verifyVerdict?.Invoke(p) ?? SteamTokenVerdict.ServiceUnavailable;
    }

    private void Broadcast(Packet packet)
    {
        Member[] array = members.Where((Member x) => x.Peer != null).ToArray();
        foreach (Member member in array)
        {
            try
            {
                member.Wire.Send(packet);
            }
            catch (Exception error)
            {
                Failure = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Broadcast failed: ", error);
                Remove(member);
            }
        }
    }

    public void AllowSavedBaseline(string fingerprint)
    {
        compatibleBaselines.Add(fingerprint);
    }

    private void Remove(Member member)
    {
        if (members.Remove(member))
        {
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
