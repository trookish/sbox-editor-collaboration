using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// A stand-in for the parts of Cloudflare's API the relay deploy uses, served over real HTTP on a loopback port so the deployer's requests (headers, multipart
/// body, call order) are checked as they go over the wire. It is written from Cloudflare's documented behaviour and from what Wrangler sends; it is not
/// Cloudflare, and a pass against it says the editor sends what Wrangler sends, not that Cloudflare accepted it.
/// </summary>
public sealed class FakeCloudflare : IDisposable
{
    public sealed record Call(string Method, string Path, string Authorization, string ScriptApiDate, string ContentType, byte[] Body);

    public sealed record Part(string Name, string FileName, string ContentType, string Body);

    private readonly HttpListener listener = new HttpListener();
    private readonly Task loop;
    private int healthCalls;

    public string Token { get; init; } = "cfut_TestTokenAbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
    public string AccountId { get; init; } = "acct-1";
    public string AccountName { get; init; } = "Test Account";

    /// <summary>The account's workers.dev name, or null for an account that never registered one.</summary>
    public string Subdomain { get; set; } = "tester";

    /// <summary>Names the account registration refuses as already taken.</summary>
    public HashSet<string> TakenNames { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The migration tag the existing relay has applied, or null when there is no relay yet.</summary>
    public string ExistingTag { get; set; }

    public int HealthFailuresBeforeOk { get; set; }

    /// <summary>The service name the health answer claims. Anything but "collab-relay" is some other site answering at the address.</summary>
    public string HealthService { get; set; } = "collab-relay";

    /// <summary>Makes an error response echo the token back, to prove the editor removes it before showing anything.</summary>
    public bool EchoTokenInErrors { get; set; }

    public int? FailUploadWithStatus { get; set; }

    public List<Call> Calls { get; } = new List<Call>();
    public Dictionary<string, Part> UploadedParts { get; } = new Dictionary<string, Part>(StringComparer.Ordinal);
    public JsonObject UploadedMetadata { get; private set; }
    public int Port { get; }

    public string HealthAnswerHost { get; private set; }

    public FakeCloudflare()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
        listener.Start();
        loop = Task.Run(Serve);
    }

    public string HealthUrl(string host) => "http://127.0.0.1:" + Port + "/health?host=" + Uri.EscapeDataString(host);

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Handle(context); }
            catch (Exception ex) { Reply(context, 500, new JsonObject { ["success"] = false, ["errors"] = new JsonArray { new JsonObject { ["code"] = 1, ["message"] = "fake failed: " + ex.Message } } }); }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        byte[] body;
        using (var memory = new MemoryStream())
        {
            request.InputStream.CopyTo(memory);
            body = memory.ToArray();
        }
        string path = request.Url.AbsolutePath;
        string authorization = request.Headers["Authorization"];
        lock (Calls)
        {
            Calls.Add(new Call(request.HttpMethod, path + request.Url.Query, authorization, request.Headers["Cloudflare-Workers-Script-Api-Date"], request.ContentType, body));
        }

        if (path == "/health")
        {
            HealthAnswerHost = request.QueryString["host"];
            if (Interlocked.Increment(ref healthCalls) <= HealthFailuresBeforeOk)
            {
                ReplyText(context, 503, "starting");
                return;
            }
            Reply(context, 200, new JsonObject { ["status"] = "ok", ["service"] = HealthService, ["protocol"] = 1 });
            return;
        }

        string prefix = "/client/v4";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            ReplyText(context, 404, "not found");
            return;
        }
        string route = path.Substring(prefix.Length);
        if (authorization != "Bearer " + Token)
        {
            Fail(context, 401, 10000, "Authentication error" + (EchoTokenInErrors ? " for " + authorization : ""));
            return;
        }

        string account = "/accounts/" + AccountId;
        if (request.HttpMethod == "GET" && route == "/accounts")
        {
            Ok(context, new JsonArray { new JsonObject { ["id"] = AccountId, ["name"] = AccountName } });
        }
        else if (request.HttpMethod == "GET" && route == account + "/workers/subdomain")
        {
            if (Subdomain == null) Fail(context, 404, 10007, "This account has not enabled a workers.dev subdomain.");
            else Ok(context, new JsonObject { ["subdomain"] = Subdomain });
        }
        else if (request.HttpMethod == "PUT" && route == account + "/workers/subdomain")
        {
            string wanted = JsonNode.Parse(Encoding.UTF8.GetString(body))?["subdomain"]?.GetValue<string>();
            if (wanted != null && TakenNames.Contains(wanted)) Fail(context, 409, 10031, "Subdomain is unavailable.");
            else { Subdomain = wanted; Ok(context, new JsonObject { ["subdomain"] = wanted }); }
        }
        else if (request.HttpMethod == "GET" && route == account + "/workers/services/collab-relay")
        {
            if (ExistingTag == null) Fail(context, 404, 10090, "This Worker does not exist on your account.");
            else Ok(context, new JsonObject { ["default_environment"] = new JsonObject { ["script"] = new JsonObject { ["migration_tag"] = ExistingTag } } });
        }
        else if (request.HttpMethod == "PUT" && route == account + "/workers/scripts/collab-relay")
        {
            if (FailUploadWithStatus is int forced)
            {
                Fail(context, forced, 10021, "Script startup failed" + (EchoTokenInErrors ? " near " + Token : ""));
                return;
            }
            ParseMultipart(request.ContentType, body);
            string newTag = UploadedMetadata?["migrations"]?["new_tag"]?.GetValue<string>();
            if (newTag != null) ExistingTag = newTag;
            else ExistingTag ??= "v1";
            Ok(context, new JsonObject { ["id"] = "collab-relay" });
        }
        else if (request.HttpMethod == "POST" && route == account + "/workers/scripts/collab-relay/subdomain")
        {
            Ok(context, new JsonObject { ["enabled"] = true, ["previews_enabled"] = false });
        }
        else
        {
            Fail(context, 404, 7003, "No route for " + request.HttpMethod + " " + route);
        }
    }

    private void ParseMultipart(string contentType, byte[] body)
    {
        UploadedParts.Clear();
        UploadedMetadata = null;
        string boundary = null;
        foreach (string piece in (contentType ?? "").Split(';'))
        {
            string item = piece.Trim();
            if (item.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) boundary = item.Substring("boundary=".Length).Trim('"');
        }
        if (string.IsNullOrEmpty(boundary)) throw new InvalidOperationException("no multipart boundary");
        string text = Encoding.UTF8.GetString(body);
        foreach (string raw in text.Split("--" + boundary))
        {
            if (raw.Length < 4 || raw.StartsWith("--")) continue;
            int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0) continue;
            string headers = raw.Substring(0, split);
            string content = raw.Substring(split + 4);
            if (content.EndsWith("\r\n", StringComparison.Ordinal)) content = content.Substring(0, content.Length - 2);
            string name = null, fileName = null, partType = null;
            foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    name = Quoted(line, "name");
                    fileName = Quoted(line, "filename");
                }
                else if (line.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    partType = line.Substring(line.IndexOf(':') + 1).Trim();
                }
            }
            if (name == null) continue;
            UploadedParts[name] = new Part(name, fileName, partType, content);
        }
        if (UploadedParts.TryGetValue("metadata", out var metadata)) UploadedMetadata = JsonNode.Parse(metadata.Body) as JsonObject;
    }

    private static string Quoted(string header, string key)
    {
        string marker = key + "=\"";
        int at = header.IndexOf(marker, StringComparison.Ordinal);
        // "name=" also appears inside "filename=", so look for the standalone key.
        while (at > 0 && char.IsLetter(header[at - 1])) at = header.IndexOf(marker, at + 1, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + marker.Length;
        int end = header.IndexOf('"', start);
        return end < 0 ? null : header.Substring(start, end - start);
    }

    private static void Ok(HttpListenerContext context, JsonNode result) =>
        Reply(context, 200, new JsonObject { ["success"] = true, ["errors"] = new JsonArray(), ["messages"] = new JsonArray(), ["result"] = result });

    private static void Fail(HttpListenerContext context, int status, int code, string message) =>
        Reply(context, status, new JsonObject { ["success"] = false, ["errors"] = new JsonArray { new JsonObject { ["code"] = code, ["message"] = message } }, ["result"] = null });

    private static void Reply(HttpListenerContext context, int status, JsonObject body) => Send(context, status, "application/json", body.ToJsonString());

    private static void ReplyText(HttpListenerContext context, int status, string text) => Send(context, status, "text/plain", text);

    private static void Send(HttpListenerContext context, int status, string contentType, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    public void Dispose()
    {
        try { listener.Stop(); listener.Close(); } catch (Exception) { }
        try { loop.Wait(2000); } catch (Exception) { }
    }
}

/// <summary>Sends a deploy's requests to a <see cref="FakeCloudflare"/> by swapping Cloudflare's address for the stand-in's, and nothing else.</summary>
public sealed class RewritingTransport : ICloudflareTransport
{
    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly int port;
    private readonly Func<CloudflareRequest, bool> block;

    public RewritingTransport(int port, Func<CloudflareRequest, bool> block = null)
    {
        this.port = port;
        this.block = block;
    }

    public async Task<CloudflareResponse> SendAsync(CloudflareRequest request, CancellationToken cancellationToken)
    {
        if (block != null && block(request)) return new CloudflareResponse(null, null, "connection refused");
        string url = request.Url.StartsWith(CloudflareApi.Base, StringComparison.Ordinal) ? "http://127.0.0.1:" + port + "/client/v4" + request.Url.Substring(CloudflareApi.Base.Length) : request.Url;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), url) { Content = request.Content };
        foreach (var header in request.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await Client.SendAsync(message, cancellationToken);
        return new CloudflareResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
