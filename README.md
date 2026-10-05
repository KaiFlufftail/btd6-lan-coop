# BTD6 LAN co-op

Bloons TD 6 co-op normally sends every action through one of Ninja Kiwi's relay servers.
This project replaces that relay with one that runs on your own network: a small .NET
server plus a MelonLoader mod that points the game at it. Two machines in the same house
then play co-op without their traffic leaving it.

Two co-op quality of life mods built alongside it are in the same repository.

## What it does and does not do

**It moves gameplay traffic.** Once a match starts, every action, step and desync check
goes to the local relay. Measured round trip on a home Wi-Fi network is 2 to 5 ms against
roughly 30 ms to a Ninja Kiwi relay.

**It does not replace matchmaking.** The lobby and its invite code still come from LiNK,
Ninja Kiwi's web API, over the internet. Creating or joining a game needs a connection.
The relay groups peers by the lobby code they announce when they connect, so both players
land in the same match with no extra configuration.

Making the lobby local as well was attempted and does not work the way it was tried. The
relevant methods, `Lobby.Create`, `Lobby.FindOrCreate` and `Lobby.Join`, are async. On
Il2Cpp, merely attaching a Harmony patch to an async method breaks its state machine even
if the patch declines and returns, and calling one from managed code hands back a task that
completes without ever running. Both walls were hit and confirmed. A local lobby would need
a different seam: a small HTTP server answering the LiNK matchmaking endpoints, so the
game's own async machinery runs untouched. That is not built.

## Pieces

| Path | What it is |
| --- | --- |
| `relay/` | The relay server. .NET 8, no dependencies, one file of protocol and one of routing |
| `mod/LanCoop/` | Points the game's relay host and port at yours |
| `mod/CoopShare/` | Lets co-op players upgrade and sell each other's towers |
| `mod/CoopHud/` | One quiet line per teammate showing their cash |
| `mod/NetLogger/` | Diagnostic, dumps co-op messages to the MelonLoader log |
| `PROTOCOL.md` | The relay wire protocol, reversed from a capture |
| `build-relay.sh` / `.cmd` | Builds standalone relay binaries for both platforms into `dist/` |

## Hosting, on either platform

One machine runs the relay. It does not have to be the machine that creates the lobby, and
it can be Linux or Windows; the other players only need the address it prints.

Build the standalone binaries, which bundle their own runtime so the machine running them
needs nothing installed:

```bash
./build-relay.sh
```

That writes `dist/linux-x64/btd6relay` and `dist/win-x64/btd6relay.exe`, about 13 MB each,
along with the launchers below. Windows users with the .NET SDK can build their own copy
with `build-relay.cmd` instead.

**Hosting from Linux:**

```bash
./dist/linux-x64/btd6relay 1445
```

```bash
sudo ufw allow from 192.168.0.0/16 to any port 1445 proto tcp
```

**Hosting from Windows:** double-click `start-relay.cmd`, and the first time only,
`allow-firewall.cmd`, which asks for administrator rights and opens the port to the local
network. Windows blocks inbound connections by default and a blocked port looks exactly
like a join that never arrives. The rule is scoped by remote address rather than by
firewall profile on purpose, because Windows commonly labels a home Wi-Fi network Public,
and a private-profile rule then does nothing at all.

Either way the relay prints the addresses it can be reached on and the port, which is what
every player types into the mod's relay address setting. If a Tailscale or similar address
appears in that list, friends on that network can use it too, though the firewall rule
above would need widening since their traffic does not come from the local subnet.

## Installing the mod

Build it, with `BTD6_DIR` set if the game is not at the default Steam path:

```bash
cd mod/LanCoop && dotnet build -c Release
```

Copy the dll from `bin/Release` into the game's `Mods` folder on every machine. The mod
itself is the same file on Linux and Windows: MelonLoader runs it either way, including
under Proton.

Then in BTD6, under the mod's settings, turn LAN mode on and set the relay address to the
hosting machine on every machine. Create a co-op lobby as usual and share the invite code.
The relay should log `peer N joined` for each player, with a round trip in single digit
milliseconds.

## Requirements

MelonLoader 0.7.3 and BTD Mod Helper 3.6.8, against game version 56.3. Every machine in a
match must run the same game version and the same set of mods, because lockstep means both
simulations have to agree exactly. Mods that only read, like CoopHud, are safe to run on one
machine alone; anything that changes the simulation is not.

## Security

The relay has no authentication of any kind and forwards whatever it is given to everyone
else in the same match id. That is fine on a home network and not fine on the open internet.
Bind it to the LAN interface, leave the port closed at the router, and do not run it on a
public host.

## Tested

Verified: two players, a Linux host and a Windows guest, one 105 round session of about 28
minutes with no drops, no reconnects and no desync. Shared tower upgrades confirmed working
in the same setup, and the current build confirmed connecting and playing on both machines. The relay's wire format is checked against the captured bytes by
`btd6relay --selftest`.

A Windows machine hosting the relay is verified at the protocol level: the Windows build
accepted a join from a Linux client across the LAN and answered with a correct `JSRM`. It
has not yet carried a real game.

Not tested: three or four players, a player dropping and rejoining mid-match, the host
leaving, and mismatched game versions.

Known limitation: there is no reconnect or resync support. A peer whose socket drops cannot
rejoin an in-progress match, because the relay keeps no history of the action stream to
replay. A rejoining peer does at least get its original player number back rather than a new
one, which is the prerequisite for ever adding it.

## Legal

Unofficial and unaffiliated with Ninja Kiwi. No game code or asset is included or
redistributed: the mods compile against the copy of the game already installed on the
machine, and the protocol notes are a description of observed network traffic. MIT licensed,
see `LICENSE`.
