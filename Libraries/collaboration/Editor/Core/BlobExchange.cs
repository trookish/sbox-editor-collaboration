using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace TeamCreate;

public enum BlobOffer
{
    /// <summary>Nothing the packet names is missing and nothing is waiting ahead of it: the caller handles it now.</summary>
    Process,

    /// <summary>The packet waits for geometry; <see cref="BlobExchange.TryTakeReady"/> returns it, in order, once it has arrived.</summary>
    Held,

    /// <summary>The packet was refused (a bound was reached); <see cref="BlobExchange.TakeDropped"/> says why.</summary>
    Dropped
}

/// <summary>The bytes of incomplete geometry one side may hold in total. Shared by every member of a host, so sixteen guests cannot each take the whole allowance.</summary>
public sealed class BlobBudget
{
    private readonly long limit;

    public BlobBudget( long limit = BlobLimits.MaxPendingBytes )
    {
        this.limit = limit;
    }

    public long Used { get; private set; }

    public bool TryReserve( long bytes )
    {
        if ( bytes < 0 || Used + bytes > limit ) return false;
        Used += bytes;
        return true;
    }

    public void Release( long bytes )
    {
        Used = Math.Max( 0, Used - bytes );
    }
}

/// <summary>
/// One end of the geometry channel of a connection. It does both halves: it serves the geometry the other end asks for (a queue of jobs
/// drained through a window of eight unacknowledged chunks, the same discipline as file synchronization), and it holds the scene packets
/// that name geometry not yet present, asks for it, assembles it from untrusted chunks (every rule of <see cref="FileChunkAssembler"/>, plus
/// the SHA-256 the packet named) and releases the packets in their original order when everything they name has arrived.
/// Nothing here throws on hostile input: a refusal drops the packet it concerns, says why, and the scene is synchronized again.
/// </summary>
public sealed class BlobExchange
{
    private sealed class HeldPacket
    {
        public Packet Packet;

        public List<string> Hashes;

        public DateTime At;

        /// <summary>The hashes this packet added to the wanted table, so releasing it removes exactly those.</summary>
        public List<string> Counted = new();
    }

    private sealed class SendJob
    {
        public string Hash;

        public byte[] Bytes;

        public int Offset;

        public bool Unavailable;
    }

    private sealed class Assembly
    {
        public long Total;

        public byte[] Bytes;

        public readonly HashSet<long> Received = new();

        public long ReceivedBytes;
    }

    private readonly BlobStore store;

    private readonly Action<Packet> send;

    private readonly Func<DateTime> clock;

    private readonly BlobBudget budget;

    private readonly List<HeldPacket> held = new();

    // How many held packets name each missing hash. A chunk is accepted only for a hash that is in this table: unsolicited data is ignored.
    private readonly Dictionary<string, int> wanted = new( StringComparer.Ordinal );

    private readonly HashSet<string> requested = new( StringComparer.Ordinal );

    private readonly Dictionary<string, Assembly> assemblies = new( StringComparer.Ordinal );

    private readonly Queue<SendJob> jobs = new();

    private readonly FileChunkWindow window = new();

    private readonly List<(Packet Packet, string Reason)> dropped = new();

    private readonly List<string> notices = new();

    public BlobExchange( BlobStore store, Action<Packet> send, Func<DateTime> clock, BlobBudget budget = null )
    {
        this.store = store;
        this.send = send;
        this.clock = clock;
        this.budget = budget ?? new BlobBudget();
    }

    public int HeldCount => held.Count;

    /// <summary>Bytes reserved for geometry that has started to arrive on this connection.</summary>
    public long PendingBytes { get; private set; }

    /// <summary>Geometry the other end has asked for and this end has not finished sending.</summary>
    public int QueuedBlobs => jobs.Count;

    /// <summary>Gives back the pending-bytes reservation of everything half-assembled; the connection is gone and nothing will complete.</summary>
    public void Release()
    {
        foreach ( Assembly assembly in assemblies.Values ) budget.Release( assembly.Total );
        assemblies.Clear();
        PendingBytes = 0;
        held.Clear();
        wanted.Clear();
        requested.Clear();
        jobs.Clear();
    }

    public static bool IsBlobPacket( string kind ) => kind is "blob-need" or "blob-data" or "blob-ack";

    // Only these carry object states. Everything else (presence, pings, files, settings) is never held, so live cursors stay live.
    private static bool CarriesState( string kind ) => kind is "snapshot" or "accepted" or "edit" or "subscribe";

    public static List<string> HashesOf( Packet packet )
    {
        List<string> hashes = new List<string>();
        void Add( ObjectState state )
        {
            if ( (object)state?.Data == null ) return;
            foreach ( string hash in BlobRules.Referenced( state.Data ) )
            {
                if ( !hashes.Contains( hash ) ) hashes.Add( hash );
            }
        }
        if ( packet.Objects != null )
        {
            foreach ( ObjectState state in packet.Objects ) Add( state );
        }
        if ( packet.Changes != null )
        {
            // Only After is applied; Before is the state the receiver already holds and is read as plain text by the merge.
            foreach ( Change change in packet.Changes ) Add( change?.After );
        }
        return hashes;
    }

    public BlobOffer Offer( Packet packet )
    {
        if ( !CarriesState( packet.Kind ) ) return BlobOffer.Process;
        List<string> hashes = HashesOf( packet );
        if ( held.Count == 0 && hashes.All( store.Has ) ) return BlobOffer.Process;
        if ( held.Count >= BlobLimits.MaxPendingPackets )
        {
            Drop( packet, "More than " + BlobLimits.MaxPendingPackets + " scene updates are waiting for mesh geometry that has not arrived; this update was discarded and the scene will be synchronized again." );
            return BlobOffer.Dropped;
        }
        HeldPacket entry = new HeldPacket { Packet = packet, Hashes = hashes, At = clock() };
        foreach ( string hash in hashes.Where( x => !store.Has( x ) ) )
        {
            wanted[hash] = wanted.TryGetValue( hash, out int count ) ? count + 1 : 1;
            entry.Counted.Add( hash );
        }
        held.Add( entry );
        return BlobOffer.Held;
    }

    /// <summary>The oldest held packet, once everything it names is present. Later packets wait behind it, so edits keep the order the host accepted them in.</summary>
    public bool TryTakeReady( out Packet packet )
    {
        packet = null;
        if ( held.Count == 0 ) return false;
        HeldPacket head = held[0];
        if ( !head.Hashes.All( store.Has ) ) return false;
        held.RemoveAt( 0 );
        Unwant( head );
        packet = head.Packet;
        return true;
    }

    public List<(Packet Packet, string Reason)> TakeDropped()
    {
        List<(Packet Packet, string Reason)> result = dropped.ToList();
        dropped.Clear();
        return result;
    }

    public List<string> TakeNotices()
    {
        List<string> result = notices.ToList();
        notices.Clear();
        return result;
    }

    /// <summary>Handles blob-need, blob-data and blob-ack. Malformed input is ignored or drops the packet it concerns; it never throws.</summary>
    public void Handle( Packet packet )
    {
        switch ( packet.Kind )
        {
        case "blob-need":
            Enqueue( packet.Hashes );
            break;
        case "blob-ack":
            window.Release();
            break;
        case "blob-data":
            Receive( packet );
            break;
        }
    }

    /// <summary>Per frame: expires waiting packets, asks for what is missing, sends the next chunks the window allows.</summary>
    public void Pump()
    {
        Expire();
        Request();
        Serve();
    }

    private void Enqueue( List<string> hashes )
    {
        if ( hashes == null ) return;
        foreach ( string hash in hashes.Take( BlobLimits.MaxHashesPerRequest ) )
        {
            if ( !BlobStore.ValidHash( hash ) || jobs.Count >= BlobLimits.MaxHashesPerRequest || jobs.Any( x => x.Hash == hash ) ) continue;
            jobs.Enqueue( new SendJob { Hash = hash } );
        }
    }

    private void Serve()
    {
        while ( jobs.Count > 0 )
        {
            SendJob job = jobs.Peek();
            if ( job.Bytes == null && !job.Unavailable )
            {
                if ( store.TryGet( job.Hash, out byte[] bytes ) ) job.Bytes = bytes;
                else job.Unavailable = true;
            }
            if ( job.Unavailable )
            {
                // Answered at once so the asker does not wait out the timeout for geometry that will never come.
                send( new Packet { Kind = "blob-data", Sha256 = job.Hash } );
                jobs.Dequeue();
                continue;
            }
            if ( !window.TryAcquire() ) return;
            int length = Math.Min( FileSyncLimits.ChunkSize, job.Bytes.Length - job.Offset );
            byte[] data = new byte[length];
            Array.Copy( job.Bytes, job.Offset, data, 0, length );
            send( new Packet { Kind = "blob-data", Sha256 = job.Hash, Offset = job.Offset, Total = job.Bytes.Length, Data = data } );
            job.Offset += length;
            if ( job.Offset >= job.Bytes.Length ) jobs.Dequeue();
        }
    }

    private void Request()
    {
        List<string> missing = wanted.Keys.Where( x => !requested.Contains( x ) && !store.Has( x ) ).ToList();
        for ( int i = 0; i < missing.Count; i += BlobLimits.MaxHashesPerRequest )
        {
            List<string> batch = missing.Skip( i ).Take( BlobLimits.MaxHashesPerRequest ).ToList();
            send( new Packet { Kind = "blob-need", Hashes = batch } );
            foreach ( string hash in batch ) requested.Add( hash );
        }
    }

    private void Expire()
    {
        DateTime now = clock();
        foreach ( HeldPacket packet in held.Where( x => ( now - x.At ).TotalSeconds >= BlobLimits.WaitSeconds ).ToList() )
        {
            held.Remove( packet );
            Unwant( packet );
            Drop( packet.Packet, "The mesh geometry of a scene update did not arrive within " + BlobLimits.WaitSeconds + " seconds; the update was discarded and the scene will be synchronized again." );
        }
    }

    private void Receive( Packet packet )
    {
        string hash = packet.Sha256;
        // Unsolicited, malformed or already satisfied: ignored. Only geometry a held packet is waiting for is ever accepted.
        if ( !BlobStore.ValidHash( hash ) || !wanted.ContainsKey( hash ) || store.Has( hash ) ) return;
        if ( packet.Total == 0 )
        {
            Fail( hash, "The sender no longer has the mesh geometry that a scene update names; the update was discarded and the scene will be synchronized again." );
            return;
        }
        if ( packet.Total < 0 || packet.Total > BlobLimits.MaxBlobBytes )
        {
            Fail( hash, "Mesh geometry larger than the 32 MiB collaboration limit was offered and refused; the update was discarded." );
            return;
        }
        if ( !assemblies.TryGetValue( hash, out Assembly assembly ) )
        {
            if ( !budget.TryReserve( packet.Total ) )
            {
                Fail( hash, "More than 64 MiB of mesh geometry is arriving at once; the update was discarded and the scene will be synchronized again." );
                return;
            }
            PendingBytes += packet.Total;
            assemblies[hash] = assembly = new Assembly { Total = packet.Total, Bytes = new byte[packet.Total] };
        }
        else if ( assembly.Total != packet.Total )
        {
            Fail( hash, "Mesh geometry arrived with inconsistent sizes and was discarded; the scene will be synchronized again." );
            return;
        }
        byte[] data = packet.Data ?? Array.Empty<byte>();
        if ( !FileChunkAssembler.IsValidChunk( assembly.Total, packet.Offset, data.Length ) )
        {
            Fail( hash, "Mesh geometry arrived in a form that was not valid and was discarded; the scene will be synchronized again." );
            return;
        }
        // Always acknowledged, a repeat included: the sender spent a credit on it.
        send( new Packet { Kind = "blob-ack", Sha256 = hash, Offset = packet.Offset } );
        if ( !assembly.Received.Add( packet.Offset ) ) return;
        Array.Copy( data, 0, assembly.Bytes, packet.Offset, data.Length );
        assembly.ReceivedBytes += data.Length;
        if ( assembly.ReceivedBytes < assembly.Total ) return;

        Finish( hash, assembly );
        if ( !store.Put( hash, assembly.Bytes, out string error ) )
        {
            Fail( hash, error );
        }
    }

    private void Finish( string hash, Assembly assembly )
    {
        assemblies.Remove( hash );
        PendingBytes -= assembly.Total;
        budget.Release( assembly.Total );
        requested.Remove( hash );
    }

    // The geometry named by `hash` will not arrive intact: every packet waiting for it is dropped, because applying part of an update
    // would leave a scene nobody had.
    private void Fail( string hash, string reason )
    {
        if ( assemblies.TryGetValue( hash, out Assembly assembly ) ) Finish( hash, assembly );
        requested.Remove( hash );
        foreach ( HeldPacket packet in held.Where( x => x.Hashes.Contains( hash ) ).ToList() )
        {
            held.Remove( packet );
            Unwant( packet );
            Drop( packet.Packet, reason );
        }
        wanted.Remove( hash );
    }

    // A held packet leaves the queue (released or dropped): hashes nobody waits for any more are no longer wanted, and their half-built
    // assemblies give their reservation back.
    private void Unwant( HeldPacket packet )
    {
        foreach ( string hash in packet.Counted )
        {
            if ( !wanted.TryGetValue( hash, out int count ) ) continue;
            if ( count > 1 )
            {
                wanted[hash] = count - 1;
                continue;
            }
            wanted.Remove( hash );
            requested.Remove( hash );
            if ( assemblies.TryGetValue( hash, out Assembly assembly ) ) Finish( hash, assembly );
        }
    }

    private void Drop( Packet packet, string reason )
    {
        dropped.Add( (packet, reason) );
        if ( !notices.Contains( reason ) ) notices.Add( reason );
    }
}
