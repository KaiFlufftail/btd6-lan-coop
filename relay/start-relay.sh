#!/bin/sh
# Hosts a LAN co-op relay. Pass a port number to use something other than 1445.
exec "$(dirname "$0")/btd6relay" "$@"
