using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

public sealed class UpnpPortMapper : IDisposable
{
    public enum State
    {
        Idle,
        Discovering,
        Mapping,
        Mapped,
        Failed
    }

    private enum Job
    {
        None,
        Describe,
        Add,
        Remove
    }

    private readonly record struct HttpResult(bool Ok, int Status, string Body, string Error)
    {
        public static HttpResult Failed(string error)
        {
            return new HttpResult(Ok: false, 0, null, error);
        }
    }

    public const string SearchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    public static readonly IPEndPoint SsdpEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

    public static readonly string[] ServiceTypes = new string[2] { "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" };

    public const string MappingDescription = "s&box collaboration";

    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(3L);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2L);

    public const int MaxLocations = 4;

    public const int MaxDatagramsPerPoll = 16;

    private const int MaxResponseBytes = 65536;

    private const int MaxDescriptionBytes = 262144;

    private readonly UdpClient udp;

    private readonly List<string> locations = new List<string>();

    private Task<HttpResult> pending;

    private Task<IPAddress[]> routerResolution;

    private Job job;

    private string controlUrl;

    private string serviceType;

    private DateTime searchDeadlineUtc;

    private bool searchSent;

    private bool awaitingClose;

    private bool disposed;

    private int port;

    private int locationIndex;

    public State Current { get; private set; }

    public string Status { get; private set; } = "Port mapping has not been requested.";

    public bool Busy => pending != null || job != Job.None;

    public bool Finished => awaitingClose && !Busy;

    public UpnpPortMapper()
    {
        try
        {
            udp = new UdpClient();
            udp.EnableBroadcast = true;
        }
        catch (Exception)
        {
        }
    }

    public void Map(int port)
    {
        if ((port < 1 || port > 65535) ? true : false)
        {
            throw new ArgumentOutOfRangeException("port", "A port mapping requires a TCP port between 1 and 65535.");
        }
        Abandon();
        State current = Current;
        if ((uint)(current - 2) <= 1u)
        {
            Move(State.Idle);
        }
        this.port = port;
        searchDeadlineUtc = DateTime.UtcNow + SearchTimeout;
        Move(State.Discovering);
        SetStatus("Searching the local network for a router (UPnP)…");
    }

    public void Unmap()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            if (pending != null || job != Job.Remove)
            {
                if (Current == State.Mapped && controlUrl != null)
                {
                    StartJob(Job.Remove);
                }
                else
                {
                    Abandon();
                }
            }
            awaitingClose = true;
            Move(State.Idle);
            if (!Busy)
            {
                SetStatus("Port mapping has not been requested.");
            }
        }
        catch (Exception)
        {
            awaitingClose = true;
            job = Job.None;
            pending = null;
            Move(State.Idle);
        }
    }

    public void Poll()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            if (job == Job.Remove)
            {
                PollRemove();
                return;
            }
            switch (Current)
            {
            case State.Discovering:
                PollDiscovering();
                break;
            case State.Mapping:
                PollMapping();
                break;
            }
        }
        catch (Exception error)
        {
            Fail(CollaborationDiagnostics.DescribeExternalFailure("Port mapping failed: ", error));
        }
    }

    private void PollDiscovering()
    {
        if (udp == null)
        {
            Fail("UPnP is unavailable: this machine has no usable UDP socket to search with.");
            return;
        }
        if (!searchSent)
        {
            searchSent = true;
            byte[] bytes = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
            udp.Send(bytes, bytes.Length, SsdpEndpoint);
            return;
        }
        LanDiscovery.Drain(16, () => udp.Client.Poll(0, SelectMode.SelectRead) && udp.Available > 0, () =>
        {
            IPEndPoint remoteEP = null;
            byte[] item = udp.Receive(ref remoteEP);
            return (Bytes: item, From: remoteEP);
        }, ((byte[] Bytes, IPEndPoint From) datagram) =>
        {
            if (locations.Count < 4 && TryReadLocation(Encoding.ASCII.GetString(datagram.Bytes), out var location, out var _) && !locations.Contains(location))
            {
                locations.Add(location);
            }
        });
        if (locations.Count > 0)
        {
            Move(State.Mapping);
        }
        else if (DateTime.UtcNow >= searchDeadlineUtc)
        {
            Fail("No router answered within 3 seconds (UPnP unavailable).");
        }
    }

    private void PollMapping()
    {
        if (pending == null)
        {
            if (controlUrl == null)
            {
                if (locationIndex >= locations.Count)
                {
                    Fail("The router's description could not be read (no description is available).");
                    return;
                }
                StartJob(Job.Describe);
                SetStatus("Reading the router's description…");
                pending = ExchangeAsync(new Uri(locations[locationIndex]), "GET", null, null);
                return;
            }
            if (routerResolution == null)
            {
                BeginRouterResolution();
            }
            if (routerResolution.IsCompleted && TryTakeLocalAddress(out var local))
            {
                StartJob(Job.Add);
                SetStatus("Requesting the port mapping…");
                pending = AddMappingAsync(local);
            }
        }
        else
        {
            if (!pending.IsCompleted)
            {
                return;
            }
            HttpResult result = pending.GetAwaiter().GetResult();
            Job job = this.job;
            pending = null;
            this.job = Job.None;
            if (job == Job.Describe)
            {
                if (result.Ok && TryResolveControlUrl(result.Body, locations[locationIndex], out var foundServiceType, out var text, out var _))
                {
                    serviceType = foundServiceType;
                    controlUrl = text;
                    BeginRouterResolution();
                    return;
                }
                locationIndex++;
                if (locationIndex >= locations.Count)
                {
                    Fail("The router does not expose a WAN connection service to map a port through (" + CollaborationDiagnostics.Bounded(result.Ok ? "no control URL" : result.Error) + ").");
                }
            }
            else if (result.Ok)
            {
                Move(State.Mapped);
                SetStatus("Port mapping active.");
            }
            else
            {
                Fail("The router refused the port mapping (" + CollaborationDiagnostics.Bounded(result.Error) + ").");
            }
        }
    }

    private void PollRemove()
    {
        if (pending == null)
        {
            job = Job.None;
        }
        else if (pending.IsCompleted)
        {
            pending = null;
            job = Job.None;
            SetStatus("Port mapping has not been requested.");
        }
    }

    private void StartJob(Job next)
    {
        job = next;
        if (next == Job.Remove)
        {
            pending = RemoveMappingAsync();
        }
    }

    private async Task<HttpResult> AddMappingAsync(string local)
    {
        return await ExchangeAsync(new Uri(controlUrl), "POST", BuildAddPortMappingBody(serviceType, port, local), serviceType + "#AddPortMapping");
    }

    private async Task<HttpResult> RemoveMappingAsync()
    {
        return await ExchangeAsync(new Uri(controlUrl), "POST", BuildDeletePortMappingBody(serviceType, port), serviceType + "#DeletePortMapping");
    }

    private void BeginRouterResolution()
    {
        routerResolution = ResolveRouter(new Uri(controlUrl));
        if (!routerResolution.IsCompleted)
        {
            SetStatus("Resolving the router's address…");
        }
    }

    private static Task<IPAddress[]> ResolveRouter(Uri target)
    {
        if (IPAddress.TryParse(target.Host, out IPAddress address))
        {
            return Task.FromResult(new IPAddress[1] { address });
        }
        return Dns.GetHostAddressesAsync(target.Host);
    }

    private bool TryTakeLocalAddress(out string local)
    {
        local = null;
        Task<IPAddress[]> task = routerResolution;
        routerResolution = null;
        IPAddress[] resolved;
        try
        {
            resolved = task?.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Fail(CollaborationDiagnostics.DescribeExternalFailure("The router's address could not be resolved: ", error));
            return false;
        }
        try
        {
            local = LocalAddress(new Uri(controlUrl), resolved);
        }
        catch (Exception error2)
        {
            Fail(CollaborationDiagnostics.DescribeExternalFailure("this machine has no address the router can reach: ", error2));
            return false;
        }
        return true;
    }

    internal static bool TrySelectRouter(string host, IPAddress[] resolved, out IPAddress router, out string error)
    {
        router = null;
        error = null;
        if (IPAddress.TryParse(host, out IPAddress address))
        {
            router = address;
            return true;
        }
        IPAddress[] array = resolved ?? Array.Empty<IPAddress>();
        foreach (IPAddress iPAddress in array)
        {
            if (iPAddress != null && iPAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                router = iPAddress;
                return true;
            }
        }
        error = "the router's address could not be resolved";
        return false;
    }

    private static string LocalAddress(Uri target, IPAddress[] resolved)
    {
        if (!TrySelectRouter(target.Host, resolved, out var router, out var error))
        {
            throw new IOException(error);
        }
        using UdpClient udpClient = new UdpClient();
        udpClient.Client.Connect(new IPEndPoint(router, 1));
        IPAddress address = ((IPEndPoint)udpClient.Client.LocalEndPoint).Address;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
        {
            throw new IOException("this machine has no routable address of its own");
        }
        return address.ToString();
    }

    private void Abandon()
    {
        pending = null;
        routerResolution = null;
        job = Job.None;
        searchSent = false;
        locations.Clear();
        locationIndex = 0;
        controlUrl = null;
        serviceType = null;
    }

    private bool Move(State next)
    {
        if (next == Current || !CanTransition(Current, next))
        {
            return false;
        }
        Current = next;
        return true;
    }

    private void Fail(string status)
    {
        Current = State.Failed;
        job = Job.None;
        pending = null;
        routerResolution = null;
        SetStatus(status);
    }

    private void SetStatus(string status)
    {
        Status = CollaborationDiagnostics.Bounded(status);
    }

    internal static bool CanTransition(State from, State to)
    {
        if (1 == 0)
        {
        }
        bool result;
        switch (from)
        {
        case State.Idle:
            if (to != State.Discovering)
            {
                goto IL_0096;
            }
            result = true;
            break;
        case State.Discovering:
            switch (to)
            {
            case State.Mapping:
                break;
            case State.Failed:
                goto IL_0076;
            case State.Idle:
                goto IL_007a;
            default:
                goto IL_0096;
            }
            result = true;
            break;
        case State.Mapping:
            switch (to)
            {
            case State.Mapped:
                break;
            case State.Failed:
                goto IL_0082;
            case State.Idle:
                goto IL_0086;
            default:
                goto IL_0096;
            }
            result = true;
            break;
        case State.Mapped:
            if (to != State.Idle)
            {
                goto IL_0096;
            }
            result = true;
            break;
        case State.Failed:
            if (to != State.Idle)
            {
                if (to != State.Discovering)
                {
                    goto IL_0096;
                }
                result = true;
                break;
            }
            result = true;
            break;
        default:
            goto IL_0096;
            IL_0076:
            result = true;
            break;
            IL_007a:
            result = true;
            break;
            IL_0096:
            result = false;
            break;
            IL_0086:
            result = true;
            break;
            IL_0082:
            result = true;
            break;
        }
        if (1 == 0)
        {
        }
        return result;
    }

    internal static bool TryResolveControlUrl(string description, string location, out string foundServiceType, out string controlUrl, out string error)
    {
        foundServiceType = null;
        controlUrl = null;
        error = null;
        if (string.IsNullOrWhiteSpace(description))
        {
            error = "the description was empty";
            return false;
        }
        if (description.Length > 262144)
        {
            error = "the description was larger than this build reads";
            return false;
        }
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri result) || !string.Equals(result.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            error = "the location the router announced is not an http address";
            return false;
        }
        string[] serviceTypes = ServiceTypes;
        foreach (string text in serviceTypes)
        {
            int startIndex = 0;
            while (true)
            {
                int num = description.IndexOf(text, startIndex, StringComparison.OrdinalIgnoreCase);
                if (num < 0)
                {
                    break;
                }
                startIndex = num + text.Length;
                int num2 = description.IndexOf("</service>", num, StringComparison.OrdinalIgnoreCase);
                if (num2 < 0)
                {
                    num2 = description.Length;
                }
                string text2 = TagValue(description.Substring(num, num2 - num), "controlURL");
                if (string.IsNullOrWhiteSpace(text2))
                {
                    continue;
                }
                if (text2.IndexOfAny(new char[4] { ' ', '\r', '\n', '"' }) >= 0)
                {
                    error = "the control URL the router reported is malformed";
                    return false;
                }
                if (text2.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(text2, UriKind.Absolute, out Uri result2) || !string.Equals(result2.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "the control URL the router reported is not an http address";
                        return false;
                    }
                    controlUrl = result2.AbsoluteUri;
                }
                else
                {
                    if (!text2.StartsWith("/"))
                    {
                        error = "the control URL the router reported is neither absolute nor root-relative";
                        return false;
                    }
                    if (!Uri.TryCreate(result, text2, out Uri result3))
                    {
                        error = "the control URL the router reported does not resolve";
                        return false;
                    }
                    controlUrl = result3.AbsoluteUri;
                }
                foundServiceType = text;
                return true;
            }
        }
        error = "the description contains no WAN connection service";
        return false;
    }

    private static string TagValue(string block, string name)
    {
        int num = block.IndexOf("<" + name, StringComparison.OrdinalIgnoreCase);
        if (num < 0)
        {
            return null;
        }
        int num2 = block.IndexOf('>', num);
        if (num2 < 0)
        {
            return null;
        }
        num2++;
        int num3 = block.IndexOf("</" + name, num2, StringComparison.OrdinalIgnoreCase);
        if (num3 < 0)
        {
            return null;
        }
        return block.Substring(num2, num3 - num2).Replace("&amp;", "&").Trim();
    }

    internal static bool TryReadLocation(string response, out string location, out string error)
    {
        location = null;
        error = null;
        if (string.IsNullOrWhiteSpace(response))
        {
            error = "the reply was empty";
            return false;
        }
        string[] array = response.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.None);
        if (array.Length == 0 || !array[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            error = "the reply is not an HTTP response";
            return false;
        }
        if (array[0].Length < 12 || array[0][9] != '2')
        {
            error = "the reply was not a 2xx response";
            return false;
        }
        string text = null;
        foreach (string item in array.Skip(1))
        {
            int num = item.IndexOf(':');
            if (num > 0)
            {
                string a = item.Substring(0, num).Trim();
                string text2 = item.Substring(num + 1).Trim();
                if (string.Equals(a, "LOCATION", StringComparison.OrdinalIgnoreCase))
                {
                    location = text2;
                }
                else if (string.Equals(a, "ST", StringComparison.OrdinalIgnoreCase))
                {
                    text = text2;
                }
            }
        }
        if (location == null)
        {
            error = "the reply carries no LOCATION header";
            return false;
        }
        if (text != null && !IsGatewayTarget(text))
        {
            location = null;
            error = "the reply is for a different search target";
            return false;
        }
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri result) || !string.Equals(result.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            location = null;
            error = "the LOCATION the device announced is not an http address";
            return false;
        }
        location = result.AbsoluteUri;
        return true;
    }

    private static bool IsGatewayTarget(string target)
    {
        return target.StartsWith("urn:schemas-upnp-org:device:InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) || target.StartsWith("urn:schemas-upnp-org:service:WANIPConnection", StringComparison.OrdinalIgnoreCase) || target.StartsWith("urn:schemas-upnp-org:service:WANPPPConnection", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildAddPortMappingBody(string serviceType, int port, string internalClient)
    {
        if (!ServiceTypes.Contains(serviceType))
        {
            throw new ArgumentException("A port mapping requires a WANIPConnection or WANPPPConnection service type.", "serviceType");
        }
        if ((port < 1 || port > 65535) ? true : false)
        {
            throw new ArgumentOutOfRangeException("port", "A port mapping requires a TCP port between 1 and 65535.");
        }
        if (string.IsNullOrWhiteSpace(internalClient))
        {
            throw new ArgumentException("A port mapping requires the internal address to forward to.", "internalClient");
        }
        return SoapEnvelope(serviceType, "AddPortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + port + "</NewInternalPort><NewInternalClient>" + EscapeXml(internalClient) + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + EscapeXml("s&box collaboration") + "</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
    }

    internal static string BuildDeletePortMappingBody(string serviceType, int port)
    {
        if (!ServiceTypes.Contains(serviceType))
        {
            throw new ArgumentException("A port mapping requires a WANIPConnection or WANPPPConnection service type.", "serviceType");
        }
        if ((port < 1 || port > 65535) ? true : false)
        {
            throw new ArgumentOutOfRangeException("port", "A port mapping requires a TCP port between 1 and 65535.");
        }
        return SoapEnvelope(serviceType, "DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
    }

    private static string EscapeXml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string SoapEnvelope(string serviceType, string action, string arguments)
    {
        return "<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" + arguments + "</u:" + action + "></s:Body></s:Envelope>";
    }

    internal static string BuildHttpRequest(Uri target, string method, string body, string soapAction)
    {
        string value = (string.IsNullOrEmpty(target.PathAndQuery) ? "/" : target.PathAndQuery);
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(method).Append(' ').Append(value)
            .Append(" HTTP/1.1\r\n");
        stringBuilder.Append("Host: ").Append(target.Host);
        if (!target.IsDefaultPort)
        {
            stringBuilder.Append(':').Append(target.Port);
        }
        stringBuilder.Append("\r\nConnection: close\r\n");
        if (body != null)
        {
            stringBuilder.Append("Content-Type: text/xml; charset=\"utf-8\"\r\n");
            if (soapAction != null)
            {
                stringBuilder.Append("SOAPAction: \"").Append(soapAction).Append("\"\r\n");
            }
            stringBuilder.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        }
        stringBuilder.Append("\r\n");
        if (body != null)
        {
            stringBuilder.Append(body);
        }
        return stringBuilder.ToString();
    }

    private static async Task<HttpResult> ExchangeAsync(Uri target, string method, string body, string soapAction)
    {
        try
        {
            using CancellationTokenSource deadline = new CancellationTokenSource(RequestTimeout);
            using TcpClient tcp = new TcpClient();
            await tcp.ConnectAsync(target.Host, target.Port, deadline.Token);
            NetworkStream stream = tcp.GetStream();
            byte[] request = Encoding.UTF8.GetBytes(BuildHttpRequest(target, method, body, soapAction));
            await stream.WriteAsync(request, deadline.Token);
            using MemoryStream received = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (received.Length < 65536)
            {
                int read = await stream.ReadAsync(buffer, deadline.Token);
                if (read <= 0)
                {
                    break;
                }
                received.Write(buffer, 0, read);
                if (IsComplete(received.ToArray()))
                {
                    break;
                }
            }
            byte[] data = received.ToArray();
            if (!TryParseHttpResponse(data, out var status, out var responseBody))
            {
                return HttpResult.Failed("the router sent no complete HTTP response");
            }
            if (status < 200 || status > 299)
            {
                return HttpResult.Failed("the router answered HTTP " + status);
            }
            return new HttpResult(Ok: true, status, responseBody, null);
        }
        catch (OperationCanceledException)
        {
            return HttpResult.Failed("the router did not answer within two seconds");
        }
        catch (Exception ex2)
        {
            Exception error = ex2;
            return HttpResult.Failed(CollaborationDiagnostics.DescribeExternalFailure(string.Empty, error));
        }
    }

    private static bool IsComplete(byte[] data)
    {
        if (!TrySplitHeaders(data, out var headerEnd, out var headerText))
        {
            return false;
        }
        string text = HeaderValue(headerText, "Content-Length");
        if (text == null)
        {
            return false;
        }
        int result;
        return int.TryParse(text, out result) && data.Length >= headerEnd + result;
    }

    private static bool TryParseHttpResponse(byte[] data, out int status, out string body)
    {
        status = 0;
        body = string.Empty;
        if (!TrySplitHeaders(data, out var headerEnd, out var headerText))
        {
            return false;
        }
        int num = headerText.IndexOf('\n');
        string text = ((num < 0) ? headerText : headerText.Substring(0, num)).Trim();
        string[] array = text.Split(' ');
        if (array.Length < 2 || !array[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!int.TryParse(array[1], out status))
        {
            return false;
        }
        string text2 = HeaderValue(headerText, "Content-Length");
        if (text2 != null && int.TryParse(text2, out var result))
        {
            if (data.Length < headerEnd + result)
            {
                return false;
            }
            body = Encoding.UTF8.GetString(data, headerEnd, result);
        }
        else
        {
            body = ((data.Length > headerEnd) ? Encoding.UTF8.GetString(data, headerEnd, data.Length - headerEnd) : string.Empty);
        }
        return true;
    }

    private static bool TrySplitHeaders(byte[] data, out int headerEnd, out string headerText)
    {
        headerEnd = 0;
        headerText = string.Empty;
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                headerEnd = i + 4;
                headerText = Encoding.UTF8.GetString(data, 0, i);
                return true;
            }
        }
        return false;
    }

    private static string HeaderValue(string headers, string name)
    {
        string[] array = headers.Split('\n');
        string[] array2 = array;
        foreach (string text in array2)
        {
            int num = text.IndexOf(':');
            if (num > 0 && string.Equals(text.Substring(0, num).Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return text.Substring(num + 1).Trim();
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        pending = null;
        job = Job.None;
        try
        {
            udp?.Close();
        }
        catch
        {
        }
    }
}
