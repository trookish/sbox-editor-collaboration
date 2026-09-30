using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

/// <summary>One call to Cloudflare. The transport adds nothing: the caller builds every header, including the credential.</summary>
public sealed class CloudflareRequest
{
    public string Method { get; init; }

    public string Url { get; init; }

    public Dictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public HttpContent Content { get; init; }
}

/// <summary>What came back. <see cref="Status"/> is null when nothing answered (offline, refused by the engine, timed out), and <see cref="Failure"/> says why.</summary>
public sealed record CloudflareResponse(int? Status, string Body, string Failure = null);

/// <summary>Sends a request to Cloudflare. The editor uses the engine's HTTP client; the tests use a plain one against a local stand-in.</summary>
public interface ICloudflareTransport
{
    Task<CloudflareResponse> SendAsync(CloudflareRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Everything the editor needs to know about Cloudflare's Workers API in order to deploy the relay itself, so a person with no command-line tools can set the
/// relay up from the dock. The request shapes are the ones Wrangler (Cloudflare's own tool) sends for the same Worker, and Tools/RelayWorker/test checks them
/// against what Wrangler actually puts on the wire.
/// </summary>
public static class CloudflareApi
{
    public const string Base = "https://api.cloudflare.com/client/v4";

    public const string ScriptName = "collab-relay";

    /// <summary>The module's part name in the upload. It must equal <c>main_module</c> in the metadata.</summary>
    public const string MainModule = "index.js";

    public const string DurableObjectClass = "Room";

    public const string BindingName = "ROOMS";

    public const string MigrationTag = "v1";

    public const string CompatibilityDate = "2026-09-01";

    /// <summary>Selects the current shape of the per-script workers.dev switch, as Wrangler does.</summary>
    /// <summary>Wrangler's upload query: a short response, and bindings the upload does not name are not carried over from the previous version.</summary>
    public const string UploadQuery = "?excludeScript=true&bindings_inherit=strict";

    public const string ScriptApiDateHeader = "Cloudflare-Workers-Script-Api-Date";

    public const string ScriptApiDate = "2025-08-01";

    private const int MaxTokenChars = 200;

    private const int MinTokenChars = 30;

    private static readonly string[] KnownTokenPrefixes = { "cfut_", "cfat_", "cfk_" };

    /// <summary>
    /// The page where a person creates the token, with the two permissions the deploy needs already chosen: editing Workers scripts and reading the account.
    /// Nothing else is requested, so the token cannot touch DNS, billing or anything outside Workers.
    /// </summary>
    public static string TokenPageUrl()
    {
        string permissions = "[{\"key\":\"workers_scripts\",\"type\":\"edit\"},{\"key\":\"account_settings\",\"type\":\"read\"}]";
        return "https://dash.cloudflare.com/profile/api-tokens?permissionGroupKeys=" + Uri.EscapeDataString(permissions) + "&accountId=*&zoneId=all&name=" + Uri.EscapeDataString("s&box Editor Collaboration relay");
    }

    /// <summary>Where a person deletes the token again once the relay is deployed.</summary>
    public const string TokenListUrl = "https://dash.cloudflare.com/profile/api-tokens";

    public static string RelayUrlFor(string workersDevHost)
    {
        return "wss://" + workersDevHost + "/relay";
    }

    /// <summary>The address a relay answers health checks on, derived from the relay's own address. Null when the address is not a relay address.</summary>
    public static string HealthUrlFor(string relayUrl)
    {
        if (!Uri.TryCreate(relayUrl?.Trim(), UriKind.Absolute, out Uri uri) || (uri.Scheme != "wss" && uri.Scheme != "ws"))
        {
            return null;
        }
        return (uri.Scheme == "wss" ? "https://" : "http://") + uri.Authority + "/health";
    }

    /// <summary>
    /// Decides whether the clipboard holds a Cloudflare token before anything is sent anywhere. The text goes to Cloudflare as a credential, so this refuses
    /// what is certainly something else, above all an invite code (which carries the session's encryption secret) or a relay access key.
    /// </summary>
    public static bool TryAcceptToken(string clipboard, out string token, out string problem)
    {
        token = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(clipboard))
        {
            problem = "The clipboard is empty. Copy the token from the Cloudflare page first.";
            return false;
        }
        string text = clipboard.Trim();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                problem = "The clipboard holds more than a single token. Copy only the token from the Cloudflare page. Nothing was sent.";
                return false;
            }
        }
        if (text.Length < MinTokenChars || text.Length > MaxTokenChars)
        {
            problem = "The clipboard does not hold a Cloudflare token (wrong length). Copy the token from the Cloudflare page. Nothing was sent.";
            return false;
        }
        foreach (char c in text)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!allowed)
            {
                problem = "The clipboard does not hold a Cloudflare token (it contains other characters, such as a web address). Copy the token itself. Nothing was sent.";
                return false;
            }
        }
        if (InviteCode.TryDecode(text, out _, out _))
        {
            problem = "The clipboard holds an invite code, not a Cloudflare token. Nothing was sent.";
            return false;
        }
        bool knownPrefix = false;
        foreach (string prefix in KnownTokenPrefixes)
        {
            knownPrefix |= text.StartsWith(prefix, StringComparison.Ordinal);
        }
        if (!knownPrefix && !HasBothLetterCases(text))
        {
            // Real tokens are random mixed-case text. All one case is an invite code, a hexadecimal key or a hash: something that must never be sent to Cloudflare.
            problem = "The clipboard does not look like a Cloudflare token. Copy the token from the Cloudflare page. Nothing was sent.";
            return false;
        }
        token = text;
        return true;
    }

    private static bool HasBothLetterCases(string text)
    {
        bool lower = false;
        bool upper = false;
        foreach (char c in text)
        {
            lower |= c >= 'a' && c <= 'z';
            upper |= c >= 'A' && c <= 'Z';
        }
        return lower && upper;
    }

    /// <summary>The upload's metadata part. Migrations are sent only while the Worker has not yet had them applied, exactly as Wrangler decides it.</summary>
    public static string UploadMetadataJson(bool sendMigrations, bool keepSecrets)
    {
        var metadata = new JsonObject
        {
            ["main_module"] = MainModule,
            ["bindings"] = new JsonArray
            {
                new JsonObject { ["type"] = "durable_object_namespace", ["name"] = BindingName, ["class_name"] = DurableObjectClass }
            },
            ["compatibility_date"] = CompatibilityDate
        };
        if (sendMigrations)
        {
            // SQLite-backed Durable Objects are the only kind the free plan allows.
            metadata["migrations"] = new JsonObject
            {
                ["new_tag"] = MigrationTag,
                ["steps"] = new JsonArray { new JsonObject { ["new_sqlite_classes"] = new JsonArray { DurableObjectClass } } }
            };
        }
        if (keepSecrets)
        {
            // A relay access key the owner set as a secret survives a redeploy instead of being wiped.
            metadata["keep_bindings"] = new JsonArray { "secret_text", "secret_key" };
        }
        return metadata.ToJsonString();
    }

    /// <summary>The multipart body of a script upload. The boundary is sent unquoted, which every server accepts and some do not accept quoted.</summary>
    public static MultipartFormDataContent UploadContent(string workerSource, bool sendMigrations, bool keepSecrets)
    {
        string boundary = "collab-" + Guid.NewGuid().ToString("N");
        var content = new MultipartFormDataContent(boundary);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
        var metadata = new StringContent(UploadMetadataJson(sendMigrations, keepSecrets), Encoding.UTF8, "application/json");
        metadata.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote("metadata") };
        content.Add(metadata);
        var module = new ByteArrayContent(Encoding.UTF8.GetBytes(workerSource));
        module.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        module.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(MainModule), FileName = Quote(MainModule) };
        content.Add(module);
        return content;
    }

    // .NET writes a bare name (name=index.js) unless the value carries its own quotes. Wrangler's upload always quotes (name="index.js"), and that is the
    // form Cloudflare's documentation shows, so the editor sends the same bytes rather than rely on a server accepting both.
    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    public static CloudflareRequest Request(string method, string path, string token, HttpContent content = null, bool withScriptApiDate = false, string apiBase = null)
    {
        var request = new CloudflareRequest { Method = method, Url = (apiBase ?? Base) + path, Content = content };
        request.Headers["Authorization"] = "Bearer " + token;
        if (withScriptApiDate)
        {
            request.Headers[ScriptApiDateHeader] = ScriptApiDate;
        }
        return request;
    }

    public static StringContent Json(JsonNode body)
    {
        return new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    }

    /// <summary>The value under "result" of a successful response, or null.</summary>
    public static JsonNode ResultOf(string body)
    {
        JsonObject document = ParseObject(body);
        if (document == null || !(document["success"] is JsonValue success) || !success.TryGetValue(out bool ok) || !ok)
        {
            return null;
        }
        return document["result"];
    }

    /// <summary>The first error code Cloudflare reported, or 0.</summary>
    public static int ErrorCode(string body)
    {
        JsonObject document = ParseObject(body);
        if (document?["errors"] is JsonArray errors && errors.Count > 0 && errors[0] is JsonObject first
            && first["code"] is JsonValue code && code.TryGetValue(out int value))
        {
            return value;
        }
        return 0;
    }

    private static string ErrorMessage(string body)
    {
        JsonObject document = ParseObject(body);
        if (document?["errors"] is JsonArray errors && errors.Count > 0 && errors[0] is JsonObject first
            && first["message"] is JsonValue message && message.TryGetValue(out string text))
        {
            return text;
        }
        return null;
    }

    private static JsonObject ParseObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A sentence for a person about why a call failed: what happened and what to do next. The token is removed from anything Cloudflare echoes back, so a
    /// message can be shown or logged without carrying the credential.
    /// </summary>
    public static string Explain(string step, CloudflareResponse response, string token)
    {
        if (response == null || response.Status == null)
        {
            string why = string.IsNullOrWhiteSpace(response?.Failure) ? string.Empty : " (" + Redact(response.Failure, token) + ")";
            return "Cloudflare could not be reached while " + step + why + ". Check the internet connection and try again.";
        }
        int status = response.Status.Value;
        int code = ErrorCode(response.Body);
        if (status == 401 || status == 403 || code == 10000 || code == 9109 || code == 9106)
        {
            return "Cloudflare refused the token while " + step + ". Create the token from the page the Open Cloudflare button shows, which selects the permissions the relay needs, and copy it again.";
        }
        if (status == 429)
        {
            return "Cloudflare is limiting requests while " + step + ". Wait a minute and try again.";
        }
        if (status >= 500)
        {
            return "Cloudflare reported a problem of its own (status " + status + ") while " + step + ". Try again in a few minutes.";
        }
        string message = ErrorMessage(response.Body);
        string detail = string.IsNullOrWhiteSpace(message) ? string.Empty : ": " + Bound(Redact(message, token), 200);
        return "Cloudflare did not accept the request while " + step + " (status " + status + detail + ").";
    }

    public static string Redact(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
        {
            return text;
        }
        return text.Replace(token, "[token]");
    }

    private static string Bound(string text, int max)
    {
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
