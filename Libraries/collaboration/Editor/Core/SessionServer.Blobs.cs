using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// The host's half of mapping-tool geometry synchronization (docs/BLOB-SYNC.md). Each member has one <see cref="BlobExchange"/>: it serves the
/// geometry the member asks for from <see cref="BlobStore"/> and holds the member's edits (and a first subscribe that seeds a scene) until
/// the geometry they name has arrived, so the host never rebroadcasts a state it cannot serve. All members share one pending-bytes budget.
/// </summary>
public sealed partial class SessionServer
{
    private readonly Dictionary<Member, BlobExchange> blobMembers = new();

    private readonly BlobBudget blobBudget = new();

    private DateTime lastBlobPrune = DateTime.UtcNow;

    /// <summary>The host's geometry cache. Null disables geometry synchronization and its welcome feature, as a null project root does for files.</summary>
    public BlobStore BlobStore { get; set; }

    private BlobExchange BlobsOf( Member member )
    {
        if ( BlobStore == null ) return null;
        if ( !blobMembers.TryGetValue( member, out BlobExchange exchange ) )
        {
            blobMembers[member] = exchange = new BlobExchange( BlobStore, packet => SendTo( member, packet ), () => DateTime.UtcNow, blobBudget );
        }
        return exchange;
    }

    // The next packet to handle from a member: a released held one first, then the wire. Geometry packets are consumed here, and a packet that
    // names missing geometry is held (the member is asked for it on the next pump). Before the handshake nothing is intercepted.
    private bool NextPacket( Member member, out Packet packet )
    {
        BlobExchange exchange = member.Peer == null ? null : BlobsOf( member );
        while ( true )
        {
            if ( exchange != null && exchange.TryTakeReady( out packet ) ) return true;
            if ( !member.Wire.Incoming.TryDequeue( out packet ) ) return false;
            if ( exchange == null ) return true;
            if ( BlobExchange.IsBlobPacket( packet.Kind ) )
            {
                member.LastSeen = DateTime.UtcNow;
                exchange.Handle( packet );
                continue;
            }
            // A subscribe only matters for its objects when it creates the scene; for a scene the host holds they are ignored, so their geometry is not fetched.
            if ( packet.Kind == "subscribe" && packet.Scene != null && authority.Scenes.ContainsKey( packet.Scene ) ) return true;
            BlobOffer offer = exchange.Offer( packet );
            if ( offer == BlobOffer.Process ) return true;
            ReportDropped( member, exchange );
        }
    }

    private void ForgetBlobMember( Member member )
    {
        if ( blobMembers.Remove( member, out BlobExchange exchange ) ) exchange.Release();
    }

    private void PumpBlobs()
    {
        foreach ( KeyValuePair<Member, BlobExchange> pair in blobMembers.ToArray() )
        {
            if ( !members.Contains( pair.Key ) || pair.Key.Dropped != null )
            {
                continue;
            }
            // One member's geometry transfer failing costs that member alone; it must never escape the host's pump.
            try
            {
                pair.Value.Pump();
                ReportDropped( pair.Key, pair.Value );
            }
            catch ( Exception error )
            {
                pair.Key.Dropped = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic( "Geometry transfer failed: ", error );
            }
        }
        if ( BlobStore != null && DateTime.UtcNow - lastBlobPrune > TimeSpan.FromMinutes( 5 ) )
        {
            lastBlobPrune = DateTime.UtcNow;
            BlobStore.Prune();
        }
    }

    // A packet the host refused to hold or could not complete is answered so the member is not left waiting: an edit gets the ordinary
    // rejection (the member saves its version and takes the host's scene again), a subscribe ends the attempt with the same sentence.
    private void ReportDropped( Member member, BlobExchange exchange )
    {
        foreach ( (Packet Packet, string Reason) item in exchange.TakeDropped() )
        {
            exchange.TakeNotices();
            BlobNotice = item.Reason;
            SendTo( member, new Packet
            {
                Kind = "reject",
                Id = item.Packet.Id,
                Scene = item.Packet.Kind == "subscribe" ? null : item.Packet.Scene,
                Error = item.Reason
            } );
        }
    }

    /// <summary>The latest geometry refusal on the host's side, for the host's own dock.</summary>
    public string BlobNotice { get; private set; }

    private List<string> WelcomeFeatures()
    {
        List<string> features = new List<string>();
        if ( !string.IsNullOrEmpty( FileProjectRoot ) ) features.Add( FileSyncLimits.Feature );
        if ( !string.IsNullOrEmpty( FileProjectRoot ) ) features.Add( CodeTransferLimits.Feature );
        if ( BlobStore != null ) features.Add( BlobLimits.Feature );
        return features.Count == 0 ? null : features;
    }
}
