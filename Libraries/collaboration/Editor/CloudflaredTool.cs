using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// An editor-only Cloudflare Quick Tunnel launcher. Begin only while hosting and Dispose when hosting ends: the tool requires no administrator rights,
/// downloads from one pinned URL, and runs the binary only after checking its pinned SHA-256. The tunnel carries only the relay's encrypted frames;
/// its public address is unguessable but NOT a secret, so the relay must apply its own authentication and resource limits.
///
/// Each editor-frame Pump polls tasks; no explicit thread is started and no task is synchronously waited on. Process asynchronous output events do only
/// ConcurrentQueue.Enqueue on the runtime's I/O callbacks, never editor work, so even a partial line cannot block the editor. Pump drains the queue.
/// (Polling ReadLineAsync was the alternative; it needs one outstanding read per stream and a cancelled read cannot be abandoned cleanly, whereas the
/// event pattern owns the pipe readers itself and stops them when the process is disposed.)
///
/// Running means cloudflared printed its address, NOT that the name resolves yet. Measured in the smoke test: a lookup 0.3 s after the banner got
/// NXDOMAIN, and trycloudflare.com's negative-caching TTL is 60 s, so a friend who dials in the first seconds can be locked out for a minute by their
/// own resolver. Whoever publishes the invite should wait for cloudflared's "Registered tunnel connection" line or a successful self-check first.
/// </summary>
public sealed class CloudflaredTool : IDisposable
{
    public enum Phase { Idle, Downloading, Verifying, Starting, WaitingForAddress, Running, Failed }

    private const int MaxBytes = 120 * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes( 5 );
    private static readonly TimeSpan AddressTimeout = TimeSpan.FromSeconds( 25 );

    // Live instances are released at editor hotload and process shutdown as well as by their host's Dispose call.
    private static readonly object LiveLock = new();
    private static readonly System.Collections.Generic.HashSet<CloudflaredTool> Live = new();

    private readonly ConcurrentQueue<string> output = new();
    private CancellationTokenSource downloadCancel;
    private Task downloadTask;
    private Task<byte[]> readTask;
    private Process process;
    private FileStream executableLock;
    private DateTime startedAt;
    private long received;
    private long shownMegabytes;
    private string toolPath;
    private string partPath;
    private int port;
    private bool disposed;

    static CloudflaredTool() => AppDomain.CurrentDomain.ProcessExit += ( _, _ ) => CloseAll();

    [EditorEvent.Hotload]
    private static void OnHotload() => CloseAll();

    private static void CloseAll()
    {
        CloudflaredTool[] items;
        lock ( LiveLock ) items = new System.Collections.Generic.List<CloudflaredTool>( Live ).ToArray();
        foreach ( CloudflaredTool item in items ) item.Dispose();
    }

    public Phase State { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "The tunnel is not running.";
    public string RelayUrl { get; private set; }
    public string Error { get; private set; }

    /// <summary>Idempotent while starting or running. A failed attempt can be retried after Dispose by constructing a new tool.</summary>
    public void Begin( int localPort )
    {
        if ( disposed || State != Phase.Idle ) return;
        try
        {
            // Reject invalid ports before downloading anything.
            _ = CloudflaredRules.BuildArguments( localPort );
            port = localPort;
            toolPath = Path.Combine( SceneAdapter.Root, ".collaboration", "tools", "cloudflared.exe" );
            partPath = toolPath + ".part";
            Directory.CreateDirectory( Path.GetDirectoryName( toolPath ) );
            lock ( LiveLock ) Live.Add( this );
            if ( File.Exists( toolPath ) ) BeginVerify();
            else BeginDownload();
        }
        catch ( Exception ) { Fail( "The tunnel tool could not be prepared. Check that the project folder is writable and retry hosting." ); }
    }

    private void BeginDownload()
    {
        State = Phase.Downloading;
        Interlocked.Exchange( ref received, 0 );
        shownMegabytes = 0;
        Status = DownloadStatus( 0 );
        downloadCancel = new CancellationTokenSource( DownloadTimeout );
        downloadTask = DownloadAsync( downloadCancel.Token );
    }

    // RequestStreamAsync completes on headers, unlike RequestBytesAsync. Read chunks with cancellation and cap the body BEFORE allocation or write.
    // Progress is the count of bytes actually received over the pinned size GitHub lists; that size is a denominator for the dock only, never a
    // verification substitute. The counter is written with Interlocked because the continuation's thread is the engine's choice, not ours.
    private async Task DownloadAsync( CancellationToken token )
    {
        using Stream response = await Http.RequestStreamAsync( CloudflaredPin.Url, "GET", null, null, token );
        using FileStream file = new( partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true );
        byte[] chunk = new byte[81920];
        int total = 0;
        while ( true )
        {
            int count = await response.ReadAsync( chunk.AsMemory(), token );
            if ( count == 0 ) break;
            if ( count > MaxBytes - total ) throw new InvalidDataException( "The tunnel tool download exceeded 120 MB." );
            await file.WriteAsync( chunk.AsMemory( 0, count ), token );
            total += count;
            Interlocked.Exchange( ref received, total );
        }
    }

    // Megabytes received so far, against the listed size while the download is within it. Past it, the denominator is dropped rather than shown wrong.
    private static string DownloadStatus( long bytes )
    {
        long mb = bytes / ( 1024 * 1024 );
        long of = CloudflaredPin.SizeBytes / ( 1024 * 1024 );
        return bytes <= CloudflaredPin.SizeBytes
            ? $"Downloading the tunnel tool ({mb} of {of} MB)."
            : $"Downloading the tunnel tool ({mb} MB).";
    }

    private void BeginVerify()
    {
        State = Phase.Verifying;
        Status = "Verifying the tunnel tool.";
        // Re-read on EVERY Begin. Keep a read-only sharing lock from opening through launch so a swapped binary cannot run between check and start.
        // A .part download is first checked before replacement and checked again after replacement under this lock.
        if ( File.Exists( toolPath ) )
        {
            executableLock = new FileStream( toolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true );
            if ( executableLock.Length == 0 || executableLock.Length > MaxBytes )
            {
                executableLock.Dispose();
                executableLock = null;
                File.Delete( toolPath );
                BeginDownload();
                return;
            }
            readTask = ReadBoundedAsync( executableLock );
        }
        else
        {
            // A leftover .part is untrusted too, even though our downloader bounds its own writes.
            long length = new FileInfo( partPath ).Length;
            if ( length == 0 || length > MaxBytes )
            {
                File.Delete( partPath );
                Fail( "The tunnel tool download had an invalid size. Retry hosting to download it again." );
                return;
            }
            readTask = File.ReadAllBytesAsync( partPath );
        }
    }

    private static async Task<byte[]> ReadBoundedAsync( FileStream stream )
    {
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync( bytes );
        return bytes;
    }

    public void Pump()
    {
        if ( disposed || State is Phase.Idle or Phase.Failed ) return;
        try
        {
            if ( State == Phase.Downloading && !downloadTask.IsCompleted )
            {
                // Rebuild the sentence only when the whole-megabyte count moves, so a waiting frame formats nothing.
                long bytes = Interlocked.Read( ref received );
                if ( bytes / ( 1024 * 1024 ) != shownMegabytes )
                {
                    shownMegabytes = bytes / ( 1024 * 1024 );
                    Status = DownloadStatus( bytes );
                }
            }
            if ( State == Phase.Downloading && downloadTask.IsCompleted )
            {
                downloadTask.GetAwaiter().GetResult(); // completed task; never waits on the editor frame
                downloadCancel.Dispose();
                downloadCancel = null;
                downloadTask = null;
                BeginVerify();
            }
            if ( State == Phase.Verifying && readTask.IsCompleted )
            {
                byte[] bytes = readTask.GetAwaiter().GetResult(); // completed task
                readTask = null;
                string problem = CloudflaredRules.GetDownloadError( bytes, CloudflaredPin.Sha256 );
                if ( problem != null )
                {
                    if ( executableLock != null )
                    {
                        executableLock.Dispose();
                        executableLock = null;
                        File.Delete( toolPath );
                        BeginDownload();
                    }
                    else { File.Delete( partPath ); Fail( problem + " Retry hosting to download it again." ); }
                    return;
                }
                if ( executableLock == null )
                {
                    File.Move( partPath, toolPath, true );
                    BeginVerify();
                    return;
                }
                State = Phase.Starting;
            }
            if ( State == Phase.Starting ) Start();
            if ( State is Phase.WaitingForAddress or Phase.Running )
            {
                while ( output.TryDequeue( out string line ) )
                {
                    if ( State != Phase.WaitingForAddress ) continue;
                    string address = CloudflaredRules.ParseTunnelAddress( line );
                    if ( address == null ) continue;
                    RelayUrl = CloudflaredRules.ToRelayUrl( address );
                    State = Phase.Running;
                    Status = "The tunnel is running.";
                }
                if ( process.HasExited ) Fail( "The tunnel stopped unexpectedly. Retry hosting to restart it." );
                else if ( State == Phase.WaitingForAddress && DateTime.UtcNow - startedAt > AddressTimeout )
                    Fail( "The tunnel did not provide a public address within 25 seconds. Check the network connection and retry hosting." );
            }
        }
        catch ( OperationCanceledException ) { Fail( "The tunnel tool download timed out after five minutes. Check the network connection and retry hosting." ); }
        catch ( InvalidDataException ) { Fail( "The tunnel tool download exceeded 120 MB and was refused. Retry hosting after checking the network connection." ); }
        catch ( Exception ) { Fail( "The tunnel could not start. Check the network and project folder permissions, then retry hosting." ); }
    }

    private void Start()
    {
        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = CloudflaredRules.BuildArguments( port ),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        // Avoid inheriting editor/account tokens in the environment. Windows needs SystemRoot for its own runtime services.
        process.StartInfo.Environment.Clear();
        string systemRoot = Environment.GetEnvironmentVariable( "SystemRoot" );
        if ( !string.IsNullOrEmpty( systemRoot ) ) process.StartInfo.Environment["SystemRoot"] = systemRoot;
        // Never echo cloudflared's output into the dock: it may include machine-specific paths or diagnostic material.
        process.OutputDataReceived += ( _, e ) => { if ( e.Data != null ) output.Enqueue( e.Data ); };
        process.ErrorDataReceived += ( _, e ) => { if ( e.Data != null ) output.Enqueue( e.Data ); };
        if ( !process.Start() ) throw new InvalidOperationException();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        startedAt = DateTime.UtcNow;
        State = Phase.WaitingForAddress;
        Status = "Waiting for the tunnel address.";
    }

    private void Fail( string message )
    {
        Error = message;
        Status = message;
        State = Phase.Failed;
        RelayUrl = null;
        Stop();
    }

    private void Stop()
    {
        downloadCancel?.Cancel();
        downloadCancel?.Dispose();
        downloadCancel = null;
        if ( process != null )
        {
            try { if ( !process.HasExited ) process.Kill( true ); } catch ( InvalidOperationException ) { } catch ( System.ComponentModel.Win32Exception ) { }
            process.Dispose();
            process = null;
        }
        executableLock?.Dispose();
        executableLock = null;
        // A canceled async download may still own .part until its cancellation completes. Never reuse it; BeginDownload truncates it on the next run.
        try { if ( partPath != null ) File.Delete( partPath ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
    }

    public void Dispose()
    {
        if ( disposed ) return;
        disposed = true;
        Stop();
        lock ( LiveLock ) Live.Remove( this );
    }
}
