using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// Joins a LIVE editor host through the relay from outside the engine, then prints what came back. It has no
/// real Steam token, so a host that enforces identity must refuse it; the refusal arriving through the relay,
/// decrypted and worded, is the evidence that the host accepted the relayed connection and applied its own gates.
/// </summary>
public static class JoinDriver
{
    public static async Task<int> Run(string inviteFile, string projectId)
    {
        string code = File.ReadAllText(inviteFile).Trim();
        if (!InviteCode.TryDecode(code, projectId, out InviteData data, out string decodeError))
        {
            Console.WriteLine("FAIL: the invite did not decode: " + decodeError);
            return 1;
        }
        Console.WriteLine("invite relay: " + (data.Relay ?? "(none)") + "; direct endpoints: " + (data.Endpoints?.Count ?? 0));
        byte[] key = InviteCode.DeriveKey(data.Secret, projectId);
        Wire wire;
        try
        {
            wire = await RelayGuest.ConnectAsync(() => new ClientSocket(), data.Relay, data.Secret, key, 10, anyLoopbackPort: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: relay dial: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        Console.WriteLine("PASS: dialled the live host through the relay");
        wire.Send(new Packet
        {
            Kind = "hello",
            Peer = Guid.NewGuid().ToString(),
            Name = "relay-driver",
            Project = projectId,
            Token = Convert.ToHexString(key),
            ProtocolVersion = 4,
            Mode = CollaborationMode.Source,
            SteamId = "76561198000000001",
            SteamToken = new string('a', 64),
            OrgIdent = "cyn",
            LibraryVersion = LibraryInfo.LibraryVersion,
            ProjectManifest = "0000"
        });
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        Packet reply = null;
        while (DateTime.UtcNow < deadline && !wire.Incoming.TryDequeue(out reply))
        {
            if (wire.StoredFailure != null)
            {
                break;
            }
            await Task.Delay(50);
        }
        if (reply == null)
        {
            Console.WriteLine("RESULT: no reply. wire failure: " + (wire.StoredFailure ?? "(none)"));
            wire.Dispose();
            return 2;
        }
        Console.WriteLine("RESULT: kind=" + reply.Kind + "; error=" + (reply.Error ?? "(none)"));
        wire.Dispose();
        return 0;
    }
}
