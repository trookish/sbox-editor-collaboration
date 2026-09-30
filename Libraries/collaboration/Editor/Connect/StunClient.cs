using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TeamCreate;

public sealed class StunClient : IDisposable
{
    private const uint MagicCookie = 554869826u;

    private const int HeaderBytes = 20;

    private const ushort BindingRequest = 1;

    private const ushort BindingSuccess = 257;

    private const ushort MappedAddress = 1;

    private const ushort XorMappedAddress = 32;

    private static readonly (string Host, int Port)[] Servers = new (string, int)[2]
    {
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302)
    };

    private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(2L);

    private static readonly TimeSpan ServerBudget = TimeSpan.FromSeconds(1L);

    private readonly object gate = new object();

    private int index;

    private bool started;

    private bool disposed;

    private DateTime startedAt;

    private DateTime sentAt;

    private Task<IPAddress[]> resolution;

    private UdpClient socket;

    private byte[] transactionId;

    public string PublicEndPoint { get; private set; }

    public string Failure { get; private set; }

    public int ServerCount => Servers.Length;

    public int TriedCount { get; private set; }

    public void Poll()
    {
        if (PublicEndPoint != null || Failure != null || disposed)
        {
            return;
        }
        try
        {
            Step();
        }
        catch (Exception error)
        {
            Fail(CollaborationDiagnostics.DescribeExternalFailure("Public address lookup: ", error));
        }
    }

    private void Step()
    {
        DateTime utcNow = DateTime.UtcNow;
        if (!started)
        {
            started = true;
            startedAt = utcNow;
        }
        if (utcNow - startedAt >= OverallDeadline || index >= Servers.Length)
        {
            Exhaust();
        }
        else if (resolution == null)
        {
            resolution = Dns.GetHostAddressesAsync(Servers[index].Host);
        }
        else
        {
            if (!resolution.IsCompleted)
            {
                return;
            }
            if (socket == null)
            {
                if (!Begin(utcNow))
                {
                    Advance();
                }
            }
            else if (!Receive() && utcNow - sentAt >= ServerBudget)
            {
                Advance();
            }
        }
    }

    private bool Begin(DateTime now)
    {
        IPAddress iPAddress = null;
        try
        {
            IPAddress[] result = resolution.GetAwaiter().GetResult();
            foreach (IPAddress iPAddress2 in result)
            {
                if (iPAddress2.AddressFamily == AddressFamily.InterNetwork)
                {
                    iPAddress = iPAddress2;
                    break;
                }
            }
        }
        catch (Exception)
        {
            iPAddress = null;
        }
        resolution = null;
        if (iPAddress == null)
        {
            return false;
        }
        byte[] array = BuildBindingRequest(out var array2);
        UdpClient udpClient = new UdpClient();
        udpClient.Connect(new IPEndPoint(iPAddress, Servers[index].Port));
        udpClient.Send(array, array.Length);
        transactionId = array2;
        socket = udpClient;
        sentAt = now;
        TriedCount++;
        return true;
    }

    private bool Receive()
    {
        try
        {
            if (socket == null || !socket.Client.Poll(0, SelectMode.SelectRead) || socket.Available == 0)
            {
                return false;
            }
            IPEndPoint remoteEP = null;
            byte[] array = socket.Receive(ref remoteEP);
            if (!HasTransactionId(array))
            {
                return false;
            }
            if (!TryReadMappedAddress(array, out var address, out var _))
            {
                return false;
            }
            PublicEndPoint = address;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private bool HasTransactionId(byte[] bytes)
    {
        if (transactionId == null || bytes == null || bytes.Length < 20)
        {
            return false;
        }
        for (int i = 0; i < 12; i++)
        {
            if (bytes[8 + i] != transactionId[i])
            {
                return false;
            }
        }
        return true;
    }

    private void Advance()
    {
        try
        {
            socket?.Close();
        }
        catch
        {
        }
        socket = null;
        resolution = null;
        transactionId = null;
        index++;
        if (index >= Servers.Length)
        {
            Exhaust();
        }
    }

    private void Exhaust()
    {
        if (PublicEndPoint == null)
        {
            Fail((TriedCount == 0) ? "The public address could not be determined: the STUN servers could not be resolved on this network." : "The public address could not be determined: no STUN server answered within two seconds (UDP may be blocked).");
        }
    }

    private void Fail(string message)
    {
        lock (gate)
        {
            if (Failure == null)
            {
                string text = (Failure = CollaborationDiagnostics.Bounded(message));
            }
        }
        try
        {
            socket?.Close();
        }
        catch
        {
        }
        socket = null;
    }

    internal static byte[] BuildBindingRequest(out byte[] transactionId)
    {
        transactionId = RandomNumberGenerator.GetBytes(12);
        byte[] array = new byte[20]
        {
            0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };
        uint num = 554869826u;
        array[4] = (byte)(num >> 24);
        array[5] = (byte)(num >> 16);
        array[6] = (byte)(num >> 8);
        array[7] = (byte)num;
        Array.Copy(transactionId, 0, array, 8, 12);
        return array;
    }

    internal static bool TryReadMappedAddress(byte[] response, out string address, out string error)
    {
        address = null;
        error = null;
        if (response == null || response.Length < 20)
        {
            error = "The datagram is shorter than a STUN header.";
            return false;
        }
        if (ReadUInt16(response, 0) != 257)
        {
            error = "The datagram is not a STUN Binding Success Response.";
            return false;
        }
        if (ReadUInt32(response, 4) != 554869826)
        {
            error = "The datagram does not carry the STUN magic cookie.";
            return false;
        }
        ushort num = ReadUInt16(response, 2);
        if (num != response.Length - 20)
        {
            error = "The STUN response declares a length its datagram does not have.";
            return false;
        }
        int i = 20;
        ushort num4;
        for (int num2 = 20 + num; i + 4 <= num2; i += 4 + ((num4 + 3) & -4))
        {
            ushort num3 = ReadUInt16(response, i);
            num4 = ReadUInt16(response, i + 2);
            if (i + 4 + num4 > num2)
            {
                error = "The STUN response carries a truncated attribute.";
                return false;
            }
            bool flag = ((num3 == 1 || num3 == 32) ? true : false);
            if (flag && num4 >= 8 && response[i + 5] == 1)
            {
                byte[] array = new byte[4];
                Array.Copy(response, i + 8, array, 0, 4);
                if (num3 == 32)
                {
                    uint num5 = 554869826u;
                    array[0] ^= (byte)(num5 >> 24);
                    array[1] ^= (byte)(num5 >> 16);
                    array[2] ^= (byte)(num5 >> 8);
                    array[3] ^= (byte)num5;
                }
                address = array[0] + "." + array[1] + "." + array[2] + "." + array[3];
                return true;
            }
        }
        error = "The STUN response carries no IPv4 mapped address.";
        return false;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            try
            {
                socket?.Close();
            }
            catch
            {
            }
            socket = null;
        }
    }
}
