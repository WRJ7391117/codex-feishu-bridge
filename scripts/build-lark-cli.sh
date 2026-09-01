#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h:h}"
output_path="${1:-${project_dir}/build/vendor/lark-cli}"
source_tag="v1.0.92"
source_commit="6646386e0996b1ff5df640bccff834a20bcb203b"
patched_version="1.0.92-codex-feishu.3"
patch_file="${project_dir}/patches/lark-cli-v1.0.89-card-action-response.patch"
work_dir="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/codex-feishu-lark-cli.XXXXXX")"
trap '/bin/rm -rf -- "${work_dir}"' EXIT
go_bin="$(command -v go 2>/dev/null || true)"
gofmt_bin="$(command -v gofmt 2>/dev/null || true)"

if [[ -z "${go_bin}" || -z "${gofmt_bin}" ]]; then
    print -u2 "构建内置 lark-cli 需要 Go 工具链"
    exit 1
fi

/usr/bin/git -c advice.detachedHead=false clone --quiet --depth 1 --branch "${source_tag}" \
    https://github.com/larksuite/cli.git "${work_dir}/source"

actual_commit="$(/usr/bin/git -C "${work_dir}/source" rev-parse HEAD)"
if [[ "${actual_commit}" != "${source_commit}" ]]; then
    print -u2 "lark-cli 源码版本不匹配：${actual_commit}"
    exit 1
fi

/usr/bin/git -C "${work_dir}/source" apply --check "${patch_file}"
/usr/bin/git -C "${work_dir}/source" apply "${patch_file}"

(
    cd "${work_dir}/source"
    "${gofmt_bin}" -w \
        internal/event/adapter/lark/websocket/feishu.go \
        internal/event/adapter/lark/websocket/feishu_ingress_test.go
    "${go_bin}" test ./internal/event/adapter/lark/websocket

    ldflags="-s -w -X github.com/larksuite/cli/internal/build.Version=${patched_version} -X github.com/larksuite/cli/internal/build.Date=2026-09-02"
    CGO_ENABLED=0 GOOS=darwin GOARCH=arm64 "${go_bin}" build \
        -trimpath -ldflags "${ldflags}" -o "${work_dir}/lark-cli-arm64" .
    CGO_ENABLED=0 GOOS=darwin GOARCH=amd64 "${go_bin}" build \
        -trimpath -ldflags "${ldflags}" -o "${work_dir}/lark-cli-x86_64" .
)

/bin/mkdir -p "${output_path:h}"
/usr/bin/lipo -create \
    "${work_dir}/lark-cli-arm64" \
    "${work_dir}/lark-cli-x86_64" \
    -output "${output_path}"
/bin/chmod 755 "${output_path}"
print "${output_path}"
