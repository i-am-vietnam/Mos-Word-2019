#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 1 && $1 =~ ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ ]] || die 'Dùng: 04-import-packages.sh upload-id'
operation_lock
load_env
upload=/var/lib/mosword/incoming/$1
[[ -d $upload && ! -L $upload && $(realpath -e -- "$upload") == "$upload" && $(stat -c %u "$upload") == 0 ]] || die 'Upload không an toàn.'
# No symlinks, devices or user-owned files may enter the service or deletion tree.
[[ -z $(find "$upload" -xdev \( -type l -o ! -user root -o \( ! -type f ! -type d \) \) -print -quit) ]] || die 'Upload có mục không an toàn.'
python3 - "$upload" <<'PY'
import json, pathlib, sys
root = pathlib.Path(sys.argv[1])
expected = {f'Word2019_P{i:02d}' for i in range(1, 29)}
if {p.name for p in root.iterdir() if p.is_dir()} != expected:
    raise SystemExit('Cần đúng 28 thư mục Word2019_P01..P28.')
catalog = json.loads((root / 'testing-catalog.json').read_text(encoding='utf-8-sig'))
codes = [item.get('ProjectId', item.get('projectId')) for item in catalog]
if len(codes) != 28 or set(codes) != expected:
    raise SystemExit('Catalog không đủ đúng 28 project.')
PY
chown -R root:moswordsvc "$upload"
find "$upload" -type d -exec chmod 750 {} +
find "$upload" -type f -exec chmod 640 {} +
cli "$(current_release)" import-packages "$upload"
# Importer's "created" count may be zero on an idempotent retry.
count=$(postgres_sql -Atc 'SELECT CASE WHEN count(DISTINCT "Code") = 28 THEN count(DISTINCT "Code") FILTER (WHERE "Code" ~ '\''^Word2019_P(0[1-9]|1[0-9]|2[0-8])$'\'') ELSE -1 END FROM "Packages";')
[[ $count == 28 ]] || die 'Database chưa đủ 28 project; giữ upload để xử lý.'
# Recheck the deletion boundary; imports must never delete arbitrary caller paths.
[[ $(realpath -e -- "$upload") == /var/lib/mosword/incoming/"$1" && ! -L $upload ]] || die 'Đường dẫn upload đã đổi.'
[[ -z $(find "$upload" -xdev \( -type l -o ! -user root \) -print -quit) ]] || die 'Upload đã bị thay đổi.'
# Best effort only: overwrite cannot guarantee physical erasure on SSD/NVMe.
find "$upload" -xdev -type f -exec shred --iterations=1 --zero -- {} + || true
rm -rf --one-file-system -- "$upload"
printf 'Đã xác minh đủ 28 project và xóa upload plaintext.\n'
