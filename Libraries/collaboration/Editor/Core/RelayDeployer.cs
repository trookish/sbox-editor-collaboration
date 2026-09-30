using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

public sealed record DeployResult(bool Ok, string RelayUrl, string Error, bool Cancelled = false);

/// <summary>
/// Puts the relay on the person's own free Cloudflare account, from inside the editor, with the token they created once. It follows the same calls
/// Wrangler makes: find the account, make sure it has a workers.dev name, upload the Worker, switch its workers.dev address on, and wait until that address
/// answers. <see cref="DeployResult.Ok"/> is true only after the address answered, so a half-finished deploy is never reported as a relay to use.
/// Running it again on an account that already has the relay updates it in place and keeps any access key the owner set.
/// </summary>
public sealed class RelayDeployer
{
    public const int Steps = 6;

    private const int HealthAttempts = 10;

    private const int SubdomainAttempts = 5;

    private readonly ICloudflareTransport transport;

    private readonly string workerSource;

    private readonly Func<string, string> healthUrlFor;

    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    private readonly Func<string> newSubdomainName;

    private readonly string apiBase;

    /// <param name="healthUrlFor">Maps the workers.dev host to the address to check. The editor checks the real address; a test points it at a stand-in.</param>
    /// <param name="delay">How to wait between health checks. A test passes one that does not wait.</param>
    /// <param name="newSubdomainName">Makes a candidate workers.dev name for an account that has none.</param>
    /// <param name="apiBase">Cloudflare's API address. Only a test in a disposable copy ever passes another.</param>
    public RelayDeployer(ICloudflareTransport transport, string workerSource, Func<string, string> healthUrlFor = null,
        Func<TimeSpan, CancellationToken, Task> delay = null, Func<string> newSubdomainName = null, string apiBase = null)
    {
        this.apiBase = apiBase;
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.workerSource = string.IsNullOrEmpty(workerSource) ? throw new ArgumentException("The relay source is empty.", nameof(workerSource)) : workerSource;
        this.healthUrlFor = healthUrlFor ?? (host => "https://" + host + "/health");
        this.delay = delay ?? ((span, token) => Task.Delay(span, token));
        this.newSubdomainName = newSubdomainName ?? DefaultSubdomainName;
    }

    public async Task<DeployResult> RunAsync(string token, Action<string> progress, CancellationToken cancellationToken)
    {
        progress ??= _ => { };
        try
        {
            return await Deploy(token, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new DeployResult(false, null, "Setup was cancelled. Nothing was changed on this computer.", Cancelled: true);
        }
    }

    private async Task<DeployResult> Deploy(string token, Action<string> progress, CancellationToken ct)
    {
        // 1. The account the token can use.
        progress(Step(1, "finding the Cloudflare account"));
        CloudflareResponse accounts = await Send(CloudflareApi.Request("GET", "/accounts?per_page=50", token, apiBase: apiBase), ct);
        if (!(IsOk(accounts) && CloudflareApi.ResultOf(accounts.Body) is JsonArray list))
        {
            return Fail("finding the account", accounts, token);
        }
        string accountId = null;
        string accountName = null;
        foreach (JsonNode item in list)
        {
            if (item is JsonObject account && account["id"] is JsonValue id && id.TryGetValue(out string idText) && !string.IsNullOrWhiteSpace(idText))
            {
                accountId = idText;
                accountName = account["name"] is JsonValue name && name.TryGetValue(out string nameText) ? nameText : null;
                break;
            }
        }
        if (accountId == null)
        {
            return new DeployResult(false, null, "The token cannot see a Cloudflare account. Create it from the page the Open Cloudflare button shows and choose your account when it asks.");
        }
        string root = "/accounts/" + Uri.EscapeDataString(accountId);
        progress(Step(1, "using the account" + (string.IsNullOrWhiteSpace(accountName) ? string.Empty : " \"" + accountName + "\"")));

        // 2. The account's workers.dev name, made if the account has none yet.
        progress(Step(2, "checking the workers.dev name"));
        CloudflareResponse subdomain = await Send(CloudflareApi.Request("GET", root + "/workers/subdomain", token, apiBase: apiBase), ct);
        string host = null;
        if (IsOk(subdomain))
        {
            host = SubdomainHost(CloudflareApi.ResultOf(subdomain.Body));
        }
        else if (subdomain.Status != null && CloudflareApi.ErrorCode(subdomain.Body) == 10007)
        {
            SubdomainOutcome registered = await RegisterSubdomain(root, token, progress, ct);
            if (registered.Host == null)
            {
                return Fail("creating the workers.dev name", registered.Failure, token);
            }
            host = registered.Host;
        }
        else
        {
            return Fail("checking the workers.dev name", subdomain, token);
        }
        if (host == null)
        {
            return new DeployResult(false, null, "Cloudflare did not report a workers.dev name for this account. Open the Workers page on dash.cloudflare.com once, choose a name, and try again.");
        }

        // 3. Is the relay already there? Its migration tag decides whether the upload carries the one-time Durable Object migration.
        progress(Step(3, "checking for an existing relay"));
        CloudflareResponse existing = await Send(CloudflareApi.Request("GET", root + "/workers/services/" + CloudflareApi.ScriptName, token, apiBase: apiBase), ct);
        bool exists = false;
        string appliedTag = null;
        if (IsOk(existing))
        {
            exists = true;
            appliedTag = TagOf(CloudflareApi.ResultOf(existing.Body));
        }
        else if (existing.Status == null || existing.Status != 404)
        {
            return Fail("checking for an existing relay", existing, token);
        }
        bool sendMigrations = !string.Equals(appliedTag, CloudflareApi.MigrationTag, StringComparison.Ordinal);

        // 4. The Worker itself.
        progress(Step(4, exists ? "updating the relay" : "uploading the relay"));
        using (var content = CloudflareApi.UploadContent(workerSource, sendMigrations, keepSecrets: exists))
        {
            CloudflareResponse upload = await Send(CloudflareApi.Request("PUT", root + "/workers/scripts/" + CloudflareApi.ScriptName + CloudflareApi.UploadQuery, token, content, apiBase: apiBase), ct);
            if (!IsOk(upload))
            {
                return Fail("uploading the relay", upload, token);
            }
        }

        // 5. Switch its workers.dev address on.
        progress(Step(5, "switching the relay's address on"));
        using (var body = CloudflareApi.Json(new JsonObject { ["enabled"] = true }))
        {
            CloudflareResponse route = await Send(CloudflareApi.Request("POST", root + "/workers/scripts/" + CloudflareApi.ScriptName + "/subdomain", token, body, withScriptApiDate: true, apiBase: apiBase), ct);
            if (!IsOk(route))
            {
                return Fail("switching the relay's address on", route, token);
            }
        }

        // 6. Wait until the address answers. A new Worker takes a few seconds to appear.
        string workerHost = CloudflareApi.ScriptName + "." + host;
        string healthUrl = healthUrlFor(workerHost);
        for (int attempt = 1; attempt <= HealthAttempts; attempt++)
        {
            progress(Step(6, "waiting for the relay to answer (" + attempt + " of " + HealthAttempts + ")"));
            CloudflareResponse health = await Send(new CloudflareRequest { Method = "GET", Url = healthUrl }, ct);
            if (health.Status == 200 && IsRelayHealth(health.Body))
            {
                return new DeployResult(true, CloudflareApi.RelayUrlFor(workerHost), null);
            }
            if (attempt < HealthAttempts)
            {
                await delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        return new DeployResult(false, null, "The relay was uploaded but " + workerHost + " did not answer within about thirty seconds. It is not in use. Try again in a minute; nothing needs to be cleaned up.");
    }

    private sealed record SubdomainOutcome(string Host, CloudflareResponse Failure);

    private async Task<SubdomainOutcome> RegisterSubdomain(string root, string token, Action<string> progress, CancellationToken ct)
    {
        CloudflareResponse failure = null;
        for (int attempt = 0; attempt < SubdomainAttempts; attempt++)
        {
            string candidate = newSubdomainName();
            progress(Step(2, "creating the workers.dev name \"" + candidate + "\""));
            using var body = CloudflareApi.Json(new JsonObject { ["subdomain"] = candidate });
            CloudflareResponse created = await Send(CloudflareApi.Request("PUT", root + "/workers/subdomain", token, body, apiBase: apiBase), ct);
            if (IsOk(created))
            {
                return new SubdomainOutcome(SubdomainHost(CloudflareApi.ResultOf(created.Body)) ?? candidate + ".workers.dev", null);
            }
            failure = created;
            // 10031: the name is taken. Any other answer will not change with another name.
            if (created.Status == null || CloudflareApi.ErrorCode(created.Body) != 10031)
            {
                return new SubdomainOutcome(null, failure);
            }
        }
        return new SubdomainOutcome(null, failure);
    }

    private async Task<CloudflareResponse> Send(CloudflareRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CloudflareResponse response = await transport.SendAsync(request, ct);
        ct.ThrowIfCancellationRequested();
        return response ?? new CloudflareResponse(null, null, "no answer");
    }

    private static bool IsOk(CloudflareResponse response)
    {
        return response != null && response.Status is >= 200 and < 300 && CloudflareApi.ResultOf(response.Body) != null;
    }

    private static DeployResult Fail(string step, CloudflareResponse response, string token)
    {
        return new DeployResult(false, null, CloudflareApi.Explain(step, response, token));
    }

    private static string Step(int number, string text)
    {
        return "Step " + number + " of " + Steps + ": " + text + "…";
    }

    private static string SubdomainHost(JsonNode result)
    {
        if (result is JsonObject obj && obj["subdomain"] is JsonValue value && value.TryGetValue(out string name) && !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim() + ".workers.dev";
        }
        return null;
    }

    private static string TagOf(JsonNode result)
    {
        if (result is JsonObject obj && obj["default_environment"] is JsonObject environment && environment["script"] is JsonObject script
            && script["migration_tag"] is JsonValue tag && tag.TryGetValue(out string text))
        {
            return text;
        }
        return null;
    }

    private static bool IsRelayHealth(string body)
    {
        try
        {
            return JsonNode.Parse(body ?? string.Empty) is JsonObject obj && obj["status"]?.GetValue<string>() == "ok" && obj["service"]?.GetValue<string>() == "collab-relay";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is InvalidOperationException || ex is FormatException)
        {
            return false;
        }
    }

    /// <summary>A name like <c>collab-k3x9q2</c>: readable, unlikely to collide, and changeable later in the Cloudflare dashboard.</summary>
    private static string DefaultSubdomainName()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return "collab-" + new string(chars);
    }
}
