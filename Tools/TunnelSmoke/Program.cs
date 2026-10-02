using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TeamCreate;

// Real smoke test of the account-free tunnel. It downloads the PINNED cloudflared, verifies the pinned hash, serves a throwaway "/health" on a free local
// port, opens a Cloudflare Quick Tunnel to it, parses the address with the library's own CloudflaredRules, fetches <address>/health through the public
// internet, and cleans up. While it runs, that throwaway endpoint (answering only "ok") is reachable from the public internet for a few seconds.
//
// Usage: dotnet run --project Tools/TunnelSmoke -- --tmp <dir> [--capture <file>]
//   --tmp      where the executable is downloaded (never the repository; .collaboration/tools/ is the real editor's place)
//   --capture  write every line cloudflared printed, in arrival order, to this file

string tmpRoot = ArgValue( "--tmp" ) ?? Environment.GetEnvironmentVariable( "TMPDIR" ) ?? Path.GetTempPath();
string capturePath = ArgValue( "--capture" );
var clock = Stopwatch.StartNew();
void Step( string text ) => Console.WriteLine( $"[{clock.Elapsed.TotalSeconds,6:F1}s] {text}" );

string workDir = Path.Combine( tmpRoot, "tunnel-smoke-" + Guid.NewGuid().ToString( "N" ).Substring( 0, 8 ) );
Directory.CreateDirectory( workDir );
string exePath = Path.Combine( workDir, "cloudflared.exe" );
FileStream exeLock = null;
Process process = null;
System.Net.Sockets.TcpListener listener = null;
var lines = new ConcurrentQueue<string>();
var captured = new List<string>();
int exit = 1;
string address = null;

try
{
    Step( $"Pinned release {CloudflaredPin.Version}" );
    Step( $"Pinned URL {CloudflaredPin.Url}" );
    Step( $"Pinned SHA-256 {CloudflaredPin.Sha256}" );

    // 1. Download and verify, exactly as the editor tool does: the checksum, not the transport, decides whether the file may run. Streamed with a
    // 120 MB cap like the editor, and progress is the real byte count. The bound is 15 minutes, not the editor's 5: on this machine's connection the
    // 55 MB asset has taken from a few seconds to over five minutes, and a smoke run that dies in the download proves nothing about the tunnel.
    using ( var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan } )
    using ( var downloadCancel = new CancellationTokenSource( TimeSpan.FromMinutes( 15 ) ) )
    {
        var downloadClock = Stopwatch.StartNew();
        using var response = await http.GetAsync( CloudflaredPin.Url, HttpCompletionOption.ResponseHeadersRead, downloadCancel.Token );
        response.EnsureSuccessStatusCode();
        Step( $"HTTP {(int)response.StatusCode} from {response.RequestMessage.RequestUri.Host}, Content-Length {response.Content.Headers.ContentLength}" );
        using var body = await response.Content.ReadAsStreamAsync( downloadCancel.Token );
        var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        var lastReport = TimeSpan.Zero;
        int read;
        while ( ( read = await body.ReadAsync( chunk, downloadCancel.Token ) ) > 0 )
        {
            if ( buffer.Length + read > 120L * 1024 * 1024 )
                throw new Exception( "The download exceeded 120 MB and was refused." );
            buffer.Write( chunk, 0, read );
            if ( downloadClock.Elapsed - lastReport > TimeSpan.FromSeconds( 15 ) )
            {
                lastReport = downloadClock.Elapsed;
                Step( $"Downloading: {buffer.Length / 1048576} of {CloudflaredPin.SizeBytes / 1048576} MB" );
            }
        }
        byte[] bytes = buffer.ToArray();
        Step( $"Downloaded {bytes.Length} bytes in {downloadClock.Elapsed.TotalSeconds:F1} s (GitHub lists {CloudflaredPin.SizeBytes})" );
        Step( "Local SHA-256 " + Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant() );
        string error = CloudflaredRules.GetDownloadError( bytes, CloudflaredPin.Sha256 );
        if ( error != null )
            throw new Exception( "Verification refused the download: " + error );
        Step( "CloudflaredRules.GetDownloadError returned null: the download matches the pin" );
        File.WriteAllBytes( exePath, bytes );
    }

    // Verify what is ON DISK through a handle that also keeps it from being written or swapped while it runs. This is the editor tool's technique;
    // the smoke test shows that a process can still be started from a file held open like this.
    exeLock = new FileStream( exePath, FileMode.Open, FileAccess.Read, FileShare.Read );
    byte[] onDisk = new byte[exeLock.Length];
    exeLock.ReadExactly( onDisk );
    string diskError = CloudflaredRules.GetDownloadError( onDisk, CloudflaredPin.Sha256 );
    if ( diskError != null )
        throw new Exception( "The file on disk is not the pinned one: " + diskError );
    Step( "Re-hashed the file on disk through a read-only share lock: matches the pin" );

    // 2. A trivial local endpoint. A raw TcpListener on loopback, not HttpListener: http.sys matches the Host header against the registered prefix, and
    // Quick Tunnel forwards the public "<name>.trycloudflare.com" Host, so a "localhost" prefix answers 400 Invalid Hostname, while a wildcard "+" prefix
    // needs administrator rights (URL ACL) and listens on every interface. Loopback-only keeps the endpoint off the local network; only the tunnel reaches it.
    int port = FreePort();
    listener = new System.Net.Sockets.TcpListener( IPAddress.Loopback, port );
    listener.Start();
    Step( $"Local /health endpoint listening on http://localhost:{port}/health (loopback only)" );
    _ = Task.Run( async () =>
    {
        while ( true )
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run( async () =>
            {
                using ( client )
                {
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader( stream, Encoding.ASCII );
                        string requestLine = await reader.ReadLineAsync() ?? "";
                        // Read the headers to the blank line; their content is irrelevant here.
                        while ( !string.IsNullOrEmpty( await reader.ReadLineAsync() ) ) { }
                        string[] parts = requestLine.Split( ' ' );
                        bool ok = parts.Length >= 2 && parts[0] == "GET" && parts[1] == "/health";
                        string body = ok ? "ok" : "not found";
                        string head = ok ? "200 OK" : "404 Not Found";
                        byte[] reply = Encoding.ASCII.GetBytes( $"HTTP/1.1 {head}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}" );
                        await stream.WriteAsync( reply );
                    }
                    catch { }
                }
            } );
        }
    } );

    // 3. The tunnel. Arguments come from the library's rules so the smoke test runs the same command line the editor will.
    string arguments = CloudflaredRules.BuildArguments( port );
    Step( $"Starting: cloudflared.exe {arguments}" );
    var startInfo = new ProcessStartInfo
    {
        FileName = exePath,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    // The same environment CloudflaredTool gives the child: nothing but SystemRoot, so no editor or account variable is passed on. Running the smoke
    // test this way is what shows that cloudflared still opens a tunnel with that stripped environment.
    startInfo.Environment.Clear();
    string systemRoot = Environment.GetEnvironmentVariable( "SystemRoot" );
    if ( !string.IsNullOrEmpty( systemRoot ) )
        startInfo.Environment["SystemRoot"] = systemRoot;
    Step( $"Child environment: {startInfo.Environment.Count} variable(s) ({string.Join( ", ", startInfo.Environment.Keys )})" );
    process = Process.Start( startInfo );
    process.OutputDataReceived += ( _, e ) => { if ( e.Data != null ) lines.Enqueue( e.Data ); };
    process.ErrorDataReceived += ( _, e ) => { if ( e.Data != null ) lines.Enqueue( e.Data ); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    var addressClock = Stopwatch.StartNew();
    while ( address == null && addressClock.Elapsed < TimeSpan.FromSeconds( 25 ) && !process.HasExited )
    {
        Drain();
        await Task.Delay( 100 );
    }
    Drain();
    if ( address == null )
        throw new Exception( process.HasExited ? $"cloudflared exited with code {process.ExitCode} before printing an address." : "No tunnel address within 25 s." );
    Step( $"ParseTunnelAddress found {address} after {addressClock.Elapsed.TotalSeconds:F1} s" );
    Step( $"ToRelayUrl gives {CloudflaredRules.ToRelayUrl( address )}" );

    // 4. Through the public internet. Measured on 2026-10-02: the first lookup, made 0.3 s after the banner, got NXDOMAIN, and every retry for the next
    // 30 s got the same answer, because trycloudflare.com's SOA minimum (its negative-caching TTL) is 60 s and resolvers and the Windows DNS client
    // keep that NXDOMAIN for up to a minute. So: wait for cloudflared's "Registered tunnel connection" line (no request can be served before it), wait a
    // few more seconds before the FIRST lookup, and keep retrying for longer than the negative TTL. The spec's 30 s window cannot outlast that cache.
    var registerClock = Stopwatch.StartNew();
    while ( !captured.Any( l => l.Contains( "Registered tunnel connection", StringComparison.Ordinal ) ) && registerClock.Elapsed < TimeSpan.FromSeconds( 20 ) && !process.HasExited )
    {
        Drain();
        await Task.Delay( 100 );
    }
    Drain();
    bool registered = captured.Any( l => l.Contains( "Registered tunnel connection", StringComparison.Ordinal ) );
    Step( registered ? $"cloudflared registered its edge connection {registerClock.Elapsed.TotalSeconds:F1} s after the address" : "No registered connection within 20 s; trying anyway" );
    await Task.Delay( 5000 );
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds( 5 ) };
    var fetchClock = Stopwatch.StartNew();
    string result = null;
    int attempts = 0;
    while ( fetchClock.Elapsed < TimeSpan.FromSeconds( 75 ) )
    {
        attempts++;
        try
        {
            using var response = await client.GetAsync( address + "/health" );
            string text = await response.Content.ReadAsStringAsync();
            Step( $"GET {address}/health attempt {attempts}: HTTP {(int)response.StatusCode} body \"{text}\"" );
            if ( response.IsSuccessStatusCode && text == "ok" )
            {
                result = text;
                break;
            }
        }
        catch ( Exception ex )
        {
            Step( $"GET attempt {attempts} failed: {ex.GetType().Name}: {ex.Message}" );
        }
        Drain();
        await Task.Delay( 3000 );
    }
    Drain();
    if ( result != "ok" )
        throw new Exception( $"{address}/health did not return ok within 75 s ({attempts} attempts)." );
    Step( $"PASS: the public address answered \"ok\" through the tunnel in {fetchClock.Elapsed.TotalSeconds:F1} s ({attempts} attempt(s))" );
    exit = 0;
}
catch ( Exception ex )
{
    Step( "FAIL: " + ex.GetType().Name + ": " + ex.Message );
}
finally
{
    // 5. Cleanup, always.
    try
    {
        if ( process != null && !process.HasExited )
        {
            process.Kill( true );
            process.WaitForExit( 5000 );
        }
        Step( process == null ? "No process was started" : $"cloudflared stopped (exited={process.HasExited})" );
    }
    catch ( Exception ex ) { Step( "Could not stop cloudflared: " + ex.Message ); }
    try { listener?.Stop(); } catch { }
    exeLock?.Dispose();
    try
    {
        Directory.Delete( workDir, true );
        Step( $"Removed the temporary download folder (exists now: {Directory.Exists( workDir )})" );
    }
    catch ( Exception ex ) { Step( "Could not remove the temporary folder: " + ex.Message ); }

    Drain();
    Console.WriteLine( "---- first 15 lines of cloudflared output ----" );
    foreach ( string l in captured.Take( 15 ) )
        Console.WriteLine( l );
    Console.WriteLine( $"---- ({captured.Count} lines in total) ----" );
    if ( capturePath != null && captured.Count > 0 )
    {
        // The capture is committed to a public repository, so this machine's private network addresses (cloudflared logs the LAN IPv4 and the
        // link-local IPv6 its ICMP proxy uses) are replaced with documentation addresses. Nothing else is changed, and no replaced line can hold a
        // tunnel address, so the parser test over the file still sees cloudflared's real text. Loopback (the metrics server) is not private and stays.
        var privateIpv4 = new System.Text.RegularExpressions.Regex( @"\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b" );
        var linkLocalIpv6 = new System.Text.RegularExpressions.Regex( @"\bfe80::[0-9a-f:]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
        int redacted = 0;
        var written = captured.Select( l =>
        {
            string r = privateIpv4.Replace( l, "192.0.2.1" );
            r = linkLocalIpv6.Replace( r, "fe80::db8:1" );
            if ( r != l ) redacted++;
            return r;
        } ).ToList();
        File.WriteAllLines( capturePath, written );
        Console.WriteLine( $"Captured output written to {capturePath} ({redacted} line(s) had a private address replaced)" );
    }
}
Console.WriteLine( exit == 0 ? "RESULT: PASS" : "RESULT: FAIL" );
return exit;

void Drain()
{
    while ( lines.TryDequeue( out string line ) )
    {
        captured.Add( line );
        // First valid address wins, like the editor tool.
        address ??= CloudflaredRules.ParseTunnelAddress( line );
    }
}

static int FreePort()
{
    var probe = new System.Net.Sockets.TcpListener( IPAddress.Loopback, 0 );
    probe.Start();
    int port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
    probe.Stop();
    return port;
}

string ArgValue( string name )
{
    int i = Array.IndexOf( args, name );
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
