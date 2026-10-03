using System;

namespace TeamCreate;

/// <summary>
/// The process-wide geometry state of this editor: one content-addressed store shared by capture, apply and the session's exchanges
/// (on a host, by the host's server too, so a mesh the host's own editor captured is never copied to be served), and the version limiter
/// that keeps a vertex drag from streaming a new blob on every scan. See docs/BLOB-SYNC.md.
/// </summary>
public static class BlobHub
{
    public static BlobStore Store { get; } = new BlobStore( null, BlobLimits.MemoryCacheBytes, BlobLimits.DiskCacheBytes );

    public static BlobVersionLimiter Limiter { get; } = new BlobVersionLimiter( () => DateTime.UtcNow );

    /// <summary>
    /// True while this editor is connected to a host that advertises <see cref="BlobLimits.Feature"/>. Only then does capture replace inline
    /// geometry by placeholders: a host that predates the feature would store the placeholder as if it were the mesh.
    /// </summary>
    public static bool Enabled { get; set; }
}
