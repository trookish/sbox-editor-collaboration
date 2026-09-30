using System.Net;
using TeamCreate.Relay;

// Collaboration relay: a room-keyed forwarder for sealed collaboration traffic.
//
//   RELAY_ACCESS_KEYS   comma-separated keys a host must present to create a room (recommended)
//   RELAY_TRUST_FORWARDED_FOR  "true" only behind a reverse proxy you control
//   ASPNETCORE_URLS     listen address, e.g. http://0.0.0.0:8080 (terminate TLS in a proxy)
//
// It carries AES-256-GCM bytes it cannot read. See Libraries/collaboration/README.md for deployment.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
RelayOptions options = RelayOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RelayServer>();
// Nothing on this server writes request bodies to logs; keep the framework's own request logging quiet too.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

WebApplication app = builder.Build();

if (options.AccessKeys.Length == 0)
{
    app.Logger.LogWarning("RELAY_ACCESS_KEYS is empty: any client that can reach this relay may host a room. Set at least one key before exposing it to the internet.");
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", (RelayServer relay) => Results.Json(new { status = "ok", rooms = relay.RoomCount }));

app.Map(options.Path, async (HttpContext context, RelayServer relay) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        return;
    }
    using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await relay.HandleAsync(socket, ClientAddress(context, options), context.RequestAborted);
});

app.Run();

static string ClientAddress(HttpContext context, RelayOptions options)
{
    if (options.TrustForwardedFor && context.Request.Headers.TryGetValue("X-Forwarded-For", out Microsoft.Extensions.Primitives.StringValues forwarded))
    {
        string first = forwarded.ToString().Split(',')[0].Trim();
        if (IPAddress.TryParse(first, out IPAddress parsed))
        {
            return parsed.ToString();
        }
    }
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Exposes the entry point to the integration tests.</summary>
public partial class Program
{
}
