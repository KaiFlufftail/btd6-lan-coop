using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

// BTD6 LAN co-op relay. A star router that stands in for Ninja Kiwi's relay so machines on
// the same network play co-op directly. Peers connect by TCP, announce a match id in their
// join frame, and every frame one peer sends is forwarded to the others in that match. The
// relay synthesises the join response and the peer connected and disconnected notices; it
// never inspects game content. Protocol in Protocol.cs, reversed from a live capture.
//
// No authentication of any kind, by design: it is a LAN tool. Bind it to the LAN interface
// and keep the port closed at the router.

if (args.Contains("--selftest")) return SelfTest.Run();

int port = 1445;
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
if (positional.Length > 0 && !int.TryParse(positional[0], out port))
{
    Console.Error.WriteLine("usage: btd6relay [port] [bind-address] | btd6relay --selftest");
    return 2;
}

// Dual-stack by default so a hostname resolving to IPv6 still lands here. A bind address
// narrows it to one interface.
var bind = positional.Length > 1 ? IPAddress.Parse(positional[1]) : IPAddress.IPv6Any;
var matches = new ConcurrentDictionary<string, Match>();
var listener = new TcpListener(bind, port);
if (Equals(bind, IPAddress.IPv6Any)) listener.Server.DualMode = true;
listener.Start();
Log($"listening on {bind}:{port}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Handle(client));
}

async Task Handle(TcpClient client)
{
    client.NoDelay = true;                      // lockstep wants latency, not throughput
    var stream = client.GetStream();
    var ct = CancellationToken.None;
    Match? match = null;
    Peer? peer = null;
    try
    {
        var first = await Protocol.Read(stream, ct);
        if (first is null || first.Code != "JSM") { Log($"first frame not JSM ({first?.Code}); dropping"); return; }

        var (matchId, player) = Protocol.ParseJoin(first.Payload);
        match = matches.GetOrAdd(matchId, id => new Match(id));
        peer = match.Join(client, stream);
        if (peer is null)
        {
            Log($"[{matchId}] full with {Match.MaxPeers} peers, refusing another");
            match = null;
            return;
        }

        Log($"[{matchId}] peer {peer.Number} joined as '{player}' ({match.Count} in match)");

        await peer.Send(Protocol.JoinResponse(match.PeerNumbers()));
        // tell the existing peers this one arrived, and tell this one about the others
        match.BroadcastExcept(peer, Protocol.PeerConnected(peer.Number, match.PeerNumbers()));
        foreach (var other in match.Others(peer))
            await peer.Send(Protocol.PeerConnected(other.Number, match.PeerNumbers()));

        while (true)
        {
            var frame = await Protocol.Read(stream, ct);
            if (frame is null) break;

            // The real relay answers latency pings itself (captured: a 24B ECHO reply per
            // 16B ECHO request, never forwarded) and swallows the ECHR latency report.
            // Leaving pings unanswered trips the client's latency-fail check and it
            // reconnects every ~10 s.
            if (frame.Code == "ECHO") { await peer.Send(Protocol.EchoReply(frame.Payload)); continue; }
            if (frame.Code == "ECHR")
            {
                // The client's own measured round trip (int64 ms). Log it so lag has a number.
                if (frame.Payload.Length >= 8)
                {
                    long ms = BitConverter.ToInt64(frame.Payload, 0);
                    peer.NoteLatency(ms);
                    if (peer.LatencySamples % 10 == 1)
                        Log($"[{match.Id}] peer {peer.Number} rtt {ms} ms (avg {peer.AvgLatency:F0}, max {peer.MaxLatency})");
                }
                continue;
            }

            // Mod messages ride the same socket under their own code, so log anything
            // that is not the constant game-envelope traffic to confirm it arrives.
            if (frame.Code != "GE")
                Log($"[{match.Id}] peer {peer.Number} -> '{frame.Code}' {frame.Payload.Length}B, forwarding to {match.Count - 1}");

            // Game envelopes and everything else get relayed to the other peers.
            match.BroadcastExcept(peer, Protocol.Build(frame.Code, frame.Payload));
        }
    }
    catch (Exception e) { Log($"peer error: {e.Message}"); }
    finally
    {
        if (match is not null && peer is not null)
        {
            match.Leave(peer);
            Log($"[{match.Id}] peer {peer.Number} left ({match.Count} remain)");
            match.BroadcastExcept(peer, Protocol.PeerDisconnected(peer.Number, match.PeerNumbers()));
            if (match.Count == 0) matches.TryRemove(match.Id, out _);
        }
        client.Close();
    }
}

static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");

sealed class Peer(int number, TcpClient client, Stream stream)
{
    public int Number { get; } = number;
    public TcpClient Client { get; } = client;
    readonly Stream _stream = stream;
    readonly SemaphoreSlim _write = new(1, 1);

    public int LatencySamples { get; private set; }
    public double AvgLatency { get; private set; }
    public long MaxLatency { get; private set; }
    public void NoteLatency(long ms)
    {
        LatencySamples++;
        AvgLatency += (ms - AvgLatency) / LatencySamples;
        if (ms > MaxLatency) MaxLatency = ms;
    }

    public async Task Send(byte[] frame)
    {
        await _write.WaitAsync();
        try { await _stream.WriteAsync(frame); }
        catch { /* dropped; its read loop cleans up */ }
        finally { _write.Release(); }
    }
}

sealed class Match(string id)
{
    public const int MaxPeers = 4;              // the game's own co-op ceiling

    public string Id { get; } = id;
    readonly List<Peer> _peers = new();
    readonly object _gate = new();

    public int Count { get { lock (_gate) return _peers.Count; } }

    /// The lowest free number rather than a running counter: the simulation addresses
    /// players as 1 to 4, so a peer that drops and rejoins has to get its slot back
    /// instead of becoming player 3 in a two player game. Null when the match is full.
    public Peer? Join(TcpClient c, Stream s)
    {
        lock (_gate)
        {
            for (var number = 1; number <= MaxPeers; number++)
            {
                if (_peers.Any(p => p.Number == number)) continue;
                var peer = new Peer(number, c, s);
                _peers.Add(peer);
                return peer;
            }

            return null;
        }
    }

    public void Leave(Peer p) { lock (_gate) _peers.Remove(p); }

    public int[] PeerNumbers() { lock (_gate) return _peers.Select(x => x.Number).OrderBy(x => x).ToArray(); }
    public Peer[] Others(Peer p) { lock (_gate) return _peers.Where(x => x != p).ToArray(); }

    public void BroadcastExcept(Peer from, byte[] frame)
    {
        foreach (var t in Others(from)) _ = t.Send(frame);
    }
}
