#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h:h}"
app_source="${project_dir}/build/Codex 飞书桥接.app"
app_destination="/Applications/Codex 飞书桥接.app"

"${project_dir}/scripts/build-app.sh" >/dev/null

"${app_source}/Contents/Resources/bridge/install.sh"
/usr/bin/ditto "${app_source}" "${app_destination}"
/usr/bin/open "${app_destination}"
print "${app_destination}"
