using System.Text;

/// Checks the wire format against the bytes captured from Ninja Kiwi's own relay, so a
/// refactor that breaks framing fails here instead of twenty rounds into a co-op game.
/// Run with `btd6relay --selftest`; exit code 0 means every case passed.
static class SelfTest
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("frame round trip", () =>
        {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var frame = Protocol.Build("GE", payload);
            var read = ReadBack(frame);
            return read != null && read.Code == "GE" && read.Payload.SequenceEqual(payload);
        });

        failures += Check("frame header and trailer", () =>
        {
            var frame = Protocol.Build("GE", new byte[] { 9 });
            return Encoding.ASCII.GetString(frame, 0, 5) == "NKMlt"
                   && frame[5] == 1
                   && Encoding.ASCII.GetString(frame, frame.Length - 2, 2) == "Fn";
        });

        failures += Check("join frame parses to lobby code then account id", () =>
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(1);                                  // peer id
            WriteStr(w, "ABCDEF");                       // match id, the lobby invite code
            WriteStr(w, "0123456789abcdef01234567");      // player global id
            var (match, player) = Protocol.ParseJoin(ms.ToArray());
            return match == "ABCDEF" && player == "0123456789abcdef01234567";
        });

        failures += Check("join response matches the captured 17 byte payload", () =>
        {
            var read = ReadBack(Protocol.JoinResponse(new[] { 1 }));
            if (read is null || read.Code != "JSRM" || read.Payload.Length != 17) return false;

            // success, mask 1, zero word, count 1, player 1
            return read.Payload[0] == 1
                   && BitConverter.ToInt32(read.Payload, 1) == 1
                   && BitConverter.ToInt32(read.Payload, 5) == 0
                   && BitConverter.ToInt32(read.Payload, 9) == 1
                   && BitConverter.ToInt32(read.Payload, 13) == 1;
        });

        failures += Check("peer connected matches the captured 24 byte payload", () =>
        {
            var read = ReadBack(Protocol.PeerConnected(2, new[] { 1, 2 }));
            if (read is null || read.Code != "PCM" || read.Payload.Length != 24) return false;

            // joining peer 2, mask 0b11, zero word, count 2, players 1 and 2
            return BitConverter.ToInt32(read.Payload, 0) == 2
                   && BitConverter.ToInt32(read.Payload, 4) == 3
                   && BitConverter.ToInt32(read.Payload, 12) == 2
                   && BitConverter.ToInt32(read.Payload, 16) == 1
                   && BitConverter.ToInt32(read.Payload, 20) == 2;
        });

        failures += Check("echo reply is 24 bytes and echoes the timestamp", () =>
        {
            var request = new byte[16];
            BitConverter.GetBytes(1234567890123L).CopyTo(request, 0);
            BitConverter.GetBytes(2).CopyTo(request, 8);
            var read = ReadBack(Protocol.EchoReply(request));
            return read != null
                   && read.Code == "ECHO"
                   && read.Payload.Length == 24
                   && BitConverter.ToInt64(read.Payload, 0) == 1234567890123L
                   && BitConverter.ToInt32(read.Payload, 8) == 2
                   && read.Payload[12] == 1;
        });

        failures += Check("a rejoining peer gets the free slot back", () =>
        {
            var match = new Match("test");
            var first = match.Join(null!, Stream.Null, "player-one");
            var second = match.Join(null!, Stream.Null, "player-two");
            if (first is null || second is null || first.Number != 1 || second.Number != 2) return false;

            match.Leave(first);
            var rejoined = match.Join(null!, Stream.Null, "player-one");
            return rejoined is not null && rejoined.Number == 1;
        });

        failures += Check("a fifth peer is refused", () =>
        {
            var match = new Match("test");
            for (var i = 0; i < Match.MaxPeers; i++)
            {
                if (match.Join(null!, Stream.Null, $"player-{i}") is null) return false;
            }

            return match.Join(null!, Stream.Null, "player-five") is null;
        });

        failures += Check("a reconnecting account keeps its own number instead of taking a new slot", () =>
        {
            var match = new Match("test");
            var host = match.Join(null!, Stream.Null, "host-account");
            var guest = match.Join(null!, Stream.Null, "guest-account");
            if (host is null || guest is null || guest.Number != 2) return false;

            // The guest's socket died without a FIN and its client reconnected.
            var reconnected = match.Join(null!, Stream.Null, "guest-account");
            return reconnected is not null && reconnected.Number == 2 && match.Count == 2;
        });

        Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    static Protocol.Frame? ReadBack(byte[] frame)
    {
        using var ms = new MemoryStream(frame);
        return Protocol.Read(ms, CancellationToken.None).GetAwaiter().GetResult();
    }

    static void WriteStr(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    static int Check(string name, Func<bool> test)
    {
        bool passed;
        try { passed = test(); }
        catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); return 1; }

        Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {name}");
        return passed ? 0 : 1;
    }
}
