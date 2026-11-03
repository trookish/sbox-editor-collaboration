using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TeamCreate;

public sealed record DiscoveredHost(string EndPoint, int Port, string HostName, string SecretHex, string Manifest8, DateTime LastSeenUtc);

public sealed class LanDiscovery : IDisposable
{
    public static readonly TimeSpan QueryInterval = TimeSpan.FromMilliseconds(1500L);

    public static readonly TimeSpan HostExpiry = TimeSpan.FromSeconds(5L);

    public const int MaxDatagramsPerPoll = 32;

    public const int MaxTrackedHosts = 16;

    private readonly string projectIdHash8;

    private readonly Dictionary<string, DiscoveredHost> hosts = new Dictionary<string, DiscoveredHost>(StringComparer.Ordinal);

    private UdpClient socket;

    private bool boundToRendezvousPort;

    private bool hosting;

    private bool disposed;

    private int sessionPort;


    private string hostManifest8;

    private DateTime nextQueryUtc;

    private string failure;

    public IReadOnlyList<DiscoveredHost> Hosts
    {
        get
        {
            Prune(hosts, DateTime.UtcNow, HostExpiry);
            return new List<DiscoveredHost>(hosts.Values);
        }
    }

    public string Failure => failure;

    public bool CanHost => socket != null && boundToRendezvousPort && !disposed;

    public LanDiscovery(string projectIdHash8)
    {
        DiscoveryProtocol.BuildQuery(projectIdHash8);
        this.projectIdHash8 = projectIdHash8;
        Open();
    }

    public void Poll()
    {
        if (socket == null || disposed)
        {
            return;
        }
        try
        {
            DateTime now = DateTime.UtcNow;
            Drain(32, () => socket.Client.Poll(0, SelectMode.SelectRead) && socket.Available > 0, () =>
            {
                IPEndPoint remoteEP = null;
                byte[] item = socket.Receive(ref remoteEP);
                return (Bytes: item, From: remoteEP);
            }, ((byte[] Bytes, IPEndPoint From) datagram) =>
            {
                Handle(datagram.Bytes, datagram.From, now);
            });
            Prune(hosts, now, HostExpiry);
        }
        catch (Exception error)
        {
            failure = CollaborationDiagnostics.Bounded(Describe(error));
        }
    }

    public void SendQuery()
    {
        if (socket == null || disposed || hosting)
        {
            return;
        }
        DateTime utcNow = DateTime.UtcNow;
        if (utcNow < nextQueryUtc)
        {
            return;
        }
        nextQueryUtc = utcNow + QueryInterval;
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(DiscoveryProtocol.BuildQuery(projectIdHash8));
            socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, 27846));
        }
        catch (Exception error)
        {
            failure = CollaborationDiagnostics.Bounded(Describe(error));
        }
    }

    /// <summary>
    /// What a hosting editor answers to a discovery query. It names the port and the project revision but never the session secret: anyone on the
    /// network can send a query, and the secret is what the invite code is for.
    /// </summary>
    public static string HostReply(string projectIdHash8, int sessionPort, string hostName, string manifest8)
    {
        return DiscoveryProtocol.BuildReply(projectIdHash8, sessionPort, hostName, manifest8);
    }

    public void StartHost(int sessionPort, string manifest8)
    {
        HostReply(projectIdHash8, sessionPort, "host", manifest8);
        if (socket == null || disposed)
        {
            failure = CollaborationDiagnostics.Bounded("LAN discovery is unavailable: the rendezvous socket could not be opened on this machine.");
        }
        else if (!boundToRendezvousPort)
        {
            failure = CollaborationDiagnostics.Bounded($"LAN discovery cannot answer on this machine: UDP port {27846} is already in use.");
        }
        else
        {
            this.sessionPort = sessionPort;
            hostManifest8 = manifest8;
            hosts.Clear();
            hosting = true;
        }
    }

    public void StopHost()
    {
        hosting = false;
    }

    private void Open()
    {
        try
        {
            UdpClient udpClient;
            try
            {
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 27846));
            }
            catch (SocketException)
            {
                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            }
            udpClient.EnableBroadcast = true;
            socket = udpClient;
            boundToRendezvousPort = ((IPEndPoint)udpClient.Client.LocalEndPoint).Port == 27846;
        }
        catch (Exception error)
        {
            socket = null;
            failure = CollaborationDiagnostics.Bounded(Describe(error));
        }
    }

    private void Handle(byte[] bytes, IPEndPoint from, DateTime now)
    {
        if (bytes == null || from == null || bytes.Length > 512)
        {
            return;
        }
        string text = Encoding.ASCII.GetString(bytes);
        DiscoveryReply reply;
        if (DiscoveryProtocol.TryParseQuery(text, out var text2))
        {
            if (hosting && text2 == projectIdHash8)
            {
                Reply(from);
            }
        }
        else if (DiscoveryProtocol.TryParseReply(text, out reply) && !(reply.ProjectIdHash8 != projectIdHash8) && !hosting)
        {
            string text3 = from.Address.ToString();
            if (hosts.Count >= MaxTrackedHosts && !hosts.ContainsKey(text3))
            {
                DropOldest();
            }
            hosts[text3] = new DiscoveredHost(text3, reply.SessionPort, reply.HostName, reply.SecretHex, reply.Manifest8, now);
        }
    }

    private void Reply(IPEndPoint to)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(HostReply(projectIdHash8, sessionPort, Environment.MachineName, hostManifest8));
        socket.Send(bytes, bytes.Length, to);
    }

    private void DropOldest()
    {
        DiscoveredHost discoveredHost = null;
        foreach (KeyValuePair<string, DiscoveredHost> host in hosts)
        {
            if ((object)discoveredHost == null || host.Value.LastSeenUtc < discoveredHost.LastSeenUtc)
            {
                discoveredHost = host.Value;
            }
        }
        if ((object)discoveredHost != null)
        {
            hosts.Remove(discoveredHost.EndPoint);
        }
    }

    internal static int Drain<T>(int cap, Func<bool> available, Func<T> receive, Action<T> handle)
    {
        int i;
        for (i = 0; i < cap; i++)
        {
            if (!available())
            {
                break;
            }
            handle(receive());
        }
        return i;
    }

    internal static int Prune(Dictionary<string, DiscoveredHost> hosts, DateTime nowUtc, TimeSpan maxAge)
    {
        if (hosts == null || hosts.Count == 0)
        {
            return 0;
        }
        List<string> list = null;
        foreach (KeyValuePair<string, DiscoveredHost> host in hosts)
        {
            if (nowUtc - host.Value.LastSeenUtc > maxAge)
            {
                (list ?? (list = new List<string>())).Add(host.Key);
            }
        }
        if (list == null)
        {
            return 0;
        }
        foreach (string item in list)
        {
            hosts.Remove(item);
        }
        return list.Count;
    }

    private static string Describe(Exception error)
    {
        return CollaborationDiagnostics.DescribeExternalFailure("LAN discovery: ", error);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            hosting = false;
            try
            {
                socket?.Close();
            }
            catch
            {
            }
            socket = null;
            hosts.Clear();
        }
    }
}
