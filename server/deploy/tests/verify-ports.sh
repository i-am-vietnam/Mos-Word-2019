#!/usr/bin/env bash
set -euo pipefail
# Pure listener fixtures: no root operations or live host changes.
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/../common.sh"
validate_listeners tcp <<'SS'
LISTEN 0 128 0.0.0.0:22 0.0.0.0:*
LISTEN 0 511 [::]:443 [::]:*
LISTEN 0 128 127.0.0.1:5000 0.0.0.0:*
LISTEN 0 128 [::1]:5432 [::]:*
SS
validate_listeners udp <<'SS'
UNCONN 0 0 127.0.0.53%lo:53 0.0.0.0:*
UNCONN 0 0 10.0.0.2%eth0:68 0.0.0.0:*
SS
for fixture in 'tcp 0.0.0.0:8080' 'tcp [::]:80' 'tcp 0.0.0.0:5000' 'tcp [::]:5432' 'udp 0.0.0.0:5353' 'udp [::]:443'; do
    read -r protocol address <<<"$fixture"
    if (validate_listeners "$protocol" <<<"LISTEN 0 128 $address 0.0.0.0:*") 2>/dev/null; then
        die "Fixture không bị từ chối: $fixture"
    fi
done
printf 'PASS: chỉ public TCP22/443; loopback DB/app; DHCP68; từ chối TCP/UDP ngoài phạm vi.\n'
