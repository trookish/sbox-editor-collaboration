using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace TeamCreate;

public sealed class SandboxHttpTokenTransport : ISteamTokenTransport
{
    private readonly TimeSpan timeout;

    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10L);

    public static SandboxHttpTokenTransport Default { get; } = new SandboxHttpTokenTransport();

    public SandboxHttpTokenTransport()
        : this(DefaultTimeout)
    {
    }

    public SandboxHttpTokenTransport(TimeSpan timeout)
    {
        this.timeout = ((timeout <= TimeSpan.Zero) ? DefaultTimeout : timeout);
    }

    public async Task<SteamTokenHttpResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = new CancellationTokenSource(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        HttpResponseMessage response = null;
        try
        {
            using StringContent content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            response = await Http.RequestAsync(url, "POST", content, null, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return new SteamTokenHttpResponse(null, null);
        }
        catch (Exception)
        {
            return new SteamTokenHttpResponse(null, null);
        }
        using (response)
        {
            int status = (int)response.StatusCode;
            string body = null;
            if (status == 200)
            {
                try
                {
                    string text = await response.Content.ReadAsStringAsync(linked.Token);
                    if (text != null && text.Length <= 65536)
                    {
                        body = text;
                    }
                }
                catch (Exception)
                {
                    body = null;
                }
            }
            return new SteamTokenHttpResponse(status, body);
        }
    }
}
