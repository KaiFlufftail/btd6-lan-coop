#!/bin/sh
# Builds standalone relay binaries for both platforms into dist/. Neither needs .NET
# installed on the machine that runs it.
set -e
cd "$(dirname "$0")"

for rid in linux-x64 win-x64; do
    dotnet publish relay/relay.csproj -c Release -r "$rid" \
        --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none \
        -o "dist/$rid"
    rm -f "dist/$rid"/*.pdb
done

cp relay/start-relay.cmd relay/allow-firewall.cmd dist/win-x64/
cp relay/start-relay.sh dist/linux-x64/
chmod +x dist/linux-x64/start-relay.sh

echo
echo "dist/linux-x64/btd6relay      run it, or ./start-relay.sh"
echo "dist/win-x64/btd6relay.exe    run it, or double-click start-relay.cmd"
