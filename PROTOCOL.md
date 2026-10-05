# BTD6 relay wire protocol

Reversed from one packet capture of a live public co-op match (October 2026, game 56.3).
Ninja Kiwi's relays listen on TCP 1445 and speak this in plaintext; only LiNK's HTTP API
uses TLS. One connection carries both the lobby and the in-game traffic.

Account ids, lobby codes and player names from the original capture are deliberately not
reproduced here. Every example below uses placeholders.

## Frame

```
magic   "NKMlt"  (5 bytes: 4e 4b 4d 6c 74)
version 1 byte   (0x01)
bodyLen uint16 LE
body    bodyLen bytes
trailer "Fn"     (2 bytes: 46 6e)   constant terminator on every frame
```

```
body   = codeLen (uint32 LE) + code (ASCII) + payload
string = len (uint32 LE) + UTF-8 bytes
```

Bodies are capped at 32767 bytes, matching the client's own MAX_LENGTH.

## Message codes

| Code | Direction | Size | Meaning |
| --- | --- | --- | --- |
| `JSM` | client, once | varies | Join session. `int32 peerId`, `string matchId`, `string playerGlobalId` |
| `JSRM` | server, once | 17 B payload | Join response. `bool success` then the connected-player set |
| `PCM` | server | 24 B payload | A peer joined. `int32 peerId` then the connected-player set |
| `DC` | server | 24 B payload | A peer left, same shape as `PCM` |
| `ECHO` | both, ~1/s | 16 B up, 24 B down | Latency ping. The relay answers these itself and never forwards them |
| `ECHR` | client, ~1/s | 8 B | The client's own measured round trip, `int64` milliseconds |
| `GE` | both, bulk | varies | Game envelope. `payload[0]` is the sender's peer number, the rest is opaque |

Field order in `JSM` is the one trap worth calling out. `matchId` is the short lobby invite
code, shared by everyone in the lobby, and `playerGlobalId` is the player's own account id,
different for each of them. Grouping connections by the second field puts every player in a
private match of one, which looks exactly like a join that never completes.

The connected-player set, used by both `JSRM` and `PCM`:

```
int32 bitmask     bit (p-1) set per connected player number
int32 0           always zero in the capture
int32 count
int32[count]      the player numbers themselves
```

So one connected player encodes as `01 00 00 00 | 00 00 00 00 | 01 00 00 00 | 01 00 00 00`.

`GE` carries the lobby handshake, the chosen difficulty, each player's profile summary and
then the lockstep step and action stream. A relay never needs to understand any of it; mod
messages ride the same socket under their own codes and forward the same way.

## What a relay has to do

1. Accept TCP, read the first frame, require `JSM`, group the connection by `matchId`.
2. Assign a player number and reply `JSRM`.
3. Tell the other peers about the new one with `PCM`, and the new one about the others.
4. Answer `ECHO` locally. Ignoring pings trips the client's latency check and it
   reconnects roughly every ten seconds.
5. Forward every other frame to the other peers in the same match, unaltered.

Forwarding `GE` with the sender's peer-number byte untouched works in a two player game and
has not been tested with three or four.
