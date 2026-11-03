#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 0 ]] || die 'Dùng: 08-public-values.sh'
load_env
output=$(cli "$(current_release)" package-public-key)
public_key=$(grep -E '^[A-Za-z0-9+/]{43}=$' <<<"$output")
[[ ${#public_key} == 44 ]] || die 'Không đọc được đúng một Ed25519 public key.'
pin=$(openssl x509 -in /etc/mosword/tls/server.crt -pubkey -noout |
    openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | openssl base64 -A)
printf 'SPKI_SHA256=%s\nED25519_PUBLIC_KEY=%s\n' "$pin" "$public_key"
printf 'Chỉ đây là giá trị công khai cho build client; không sao chép private key lên PC build.\n'
