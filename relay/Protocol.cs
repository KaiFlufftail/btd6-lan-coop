using System.Buffers.Binary;
using System.Text;

// BTD6 relay wire protocol, reversed from a live co-op capture (see PROTOCOL.md).
// Frame: "NKMlt" | 0x01 | uint16 LE bodyLen | body | "Fn"
//   body = uint32 LE codeLen | code (ASCII) | payload
//   string field = uint32 LE len | UTF8 bytes
static class Protocol
{
    static readonly byte[] Magic = "NKMlt"u8.ToArray();
    static readonly byte[] Trailer = "Fn"u8.ToArray(); // constant terminator on every frame
    const byte Version = 0x01;
    public const int MaxBody = 32767;

    public sealed record Frame(string Code, byte[] Payload);

    public static async Task<Frame?> Read(Stream s, CancellationToken ct)
    {
        var head = new byte[8];                 // magic(5) + ver(1) + len(2)
        if (!await ReadExact(s, head, 8, ct)) return null;
        for (int i = 0; i < 5; i++) if (head[i] != Magic[i]) throw new IOException("bad magic");
        int bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6, 2));
        if (bodyLen is <= 0 or > MaxBody) throw new IOException($"bad body len {bodyLen}");

        var body = new byte[bodyLen];
        if (!await ReadExact(s, body, bodyLen, ct)) return null;
        var trailer = new byte[2];
        if (!await ReadExact(s, trailer, 2, ct)) return null;

        int codeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        string code = Encoding.ASCII.GetString(body, 4, codeLen);
        var payload = body[(4 + codeLen)..];
        return new Frame(code, payload);
    }

    public static byte[] Build(string code, ReadOnlySpan<byte> payload)
    {
        var codeBytes = Encoding.ASCII.GetBytes(code);
        int bodyLen = 4 + codeBytes.Length + payload.Length;
        var buf = new byte[8 + bodyLen + 2];
        Magic.CopyTo(buf, 0);
        buf[5] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), (ushort)bodyLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), (uint)codeBytes.Length);
        codeBytes.CopyTo(buf, 12);
        payload.CopyTo(buf.AsSpan(12 + codeBytes.Length));
        Trailer.CopyTo(buf, 8 + bodyLen);
        return buf;
    }

    // JSM payload = int32 PeerID | string MatchID | string PlayerGlobalID (ctor order
    // peerID, matchID, globalID). MatchID is the 6-char lobby/invite code shared by
    // everyone in the lobby; PlayerGlobalID is each player's own 24-hex account id.
    // Grouping by the wrong one (test 9) put each player in a private match.
    public static (string matchId, string playerGlobalId) ParseJoin(byte[] payload)
    {
        int o = 4;                              // skip PeerID
        string match = ReadStr(payload, ref o);
        string player = ReadStr(payload, ref o);
        return (match, player);
    }

    // JSRM = bool Success | bitmask ConnectedPlayers. PCM = int32 PeerID | bitmask.
    // Bitmask layout is templated from the capture and confirmed on first live test.
    public static byte[] JoinResponse(IEnumerable<int> connectedPeers)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)1);                       // Success = true
        WriteBitMask(w, connectedPeers);
        return Build("JSRM", ms.ToArray());
    }

    public static byte[] PeerConnected(int peerId, IEnumerable<int> connectedPeers)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(peerId);                        // int32 LE
        WriteBitMask(w, connectedPeers);
        return Build("PCM", ms.ToArray());
    }

    public static byte[] PeerDisconnected(int peerId, IEnumerable<int> connectedPeers)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(peerId);
        WriteBitMask(w, connectedPeers);
        return Build("DC", ms.ToArray());
    }

    // Observed JSRM/PCM encode the connected-player set as a count followed by one
    // int32 per player number. (Matches the captured 4-int tail for a 1-player set and
    // 5-int tail for the 2-player PCM.) Verify against the client parser on first test.
    // ECHO request (16B) = timestamp(8) | peerId(4) | isReply(4)=0.
    // Reply (24B, from the capture) = same timestamp(8) | same peerId(4) | isReply(4)=1 | 8 zero bytes.
    public static byte[] EchoReply(byte[] request)
    {
        var reply = new byte[24];
        Array.Copy(request, 0, reply, 0, Math.Min(12, request.Length));
        reply[12] = 1;
        return Build("ECHO", reply);
    }

    static void WriteBitMask(BinaryWriter w, IEnumerable<int> peers)
    {
        // Confirmed against the capture: int32 bitmask (bit p-1 per connected player),
        // an int32 zero word, int32 count, then each player number as int32.
        // {1} -> 01 00 00 00 | 00 00 00 00 | 01 00 00 00 | 01 00 00 00  (JSRM, 16B)
        // {1,2} -> 03 .. | 00 .. | 02 .. | 01 .. | 02 ..               (PCM tail)
        var list = peers.ToArray();
        int mask = 0;
        foreach (var p in list) mask |= 1 << (p - 1);
        w.Write(mask);
        w.Write(0);
        w.Write(list.Length);
        foreach (var p in list) w.Write(p);
    }

    static string ReadStr(byte[] b, ref int o)
    {
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
        o += 4;
        string s = Encoding.UTF8.GetString(b, o, len);
        o += len;
        return s;
    }

    static async Task<bool> ReadExact(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
