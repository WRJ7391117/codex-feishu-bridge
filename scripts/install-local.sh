#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h:h}"
app_source="${project_dir}/build/Codex 飞书桥接.app"
app_destination="/Applications/Codex 飞书桥接.app"
log_dir="${HOME}/.codex/log"
lock_file="${log_dir}/feishu-bridge-app-update.lock"

/bin/mkdir -p "${log_dir}"
/bin/chmod 700 "${log_dir}"
if ! /usr/bin/shlock -f "${lock_file}" -p $$; then
    print -u2 "另一个安装或更新正在进行，请稍后重试"
    exit 1
fi
trap '/bin/rm -f "${lock_file}"' EXIT HUP INT TERM

"${project_dir}/scripts/build-app.sh" >/dev/null

"${app_source}/Contents/Resources/bridge/install.sh"
/usr/bin/ditto "${app_source}" "${app_destination}"
/usr/bin/open "${app_destination}"
print "${app_destination}"
