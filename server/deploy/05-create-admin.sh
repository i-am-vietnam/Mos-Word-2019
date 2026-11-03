#!/usr/bin/env bash
set -euo pipefail
# Never enable shell tracing around credential input or CLI execution.
set +x
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 1 && $1 =~ ^[A-Za-z0-9_.@-]{1,64}$ ]] || die 'Dùng: 05-create-admin.sh username'
operation_lock
load_env
trap 'unset MOSWORD_ADMIN_PASSWORD confirmation' EXIT
IFS= read -r -s -p 'Mật khẩu admin: ' MOSWORD_ADMIN_PASSWORD </dev/tty
printf '\n' >/dev/tty
IFS= read -r -s -p 'Nhập lại mật khẩu: ' confirmation </dev/tty
printf '\n' >/dev/tty
[[ -n $MOSWORD_ADMIN_PASSWORD && $MOSWORD_ADMIN_PASSWORD == "$confirmation" ]] || die 'Mật khẩu trống hoặc không khớp.'
unset confirmation
export MOSWORD_ADMIN_PASSWORD
cli "$(current_release)" create-admin "$1"
unset MOSWORD_ADMIN_PASSWORD
printf 'Đã tạo admin đầu tiên.\n'
