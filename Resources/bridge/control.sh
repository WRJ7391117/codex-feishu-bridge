#!/bin/zsh

set -u

label="com.deepori.codex-feishu-bridge"
domain="gui/$(/usr/bin/id -u)"
service="${domain}/${label}"
support_dir="${HOME}/Library/Application Support/Codex Feishu Bridge"
plist="${HOME}/Library/LaunchAgents/${label}.plist"

is_loaded() {
    /bin/launchctl print "${service}" >/dev/null 2>&1
}

is_running() {
    /bin/launchctl print "${service}" 2>/dev/null | /usr/bin/grep -q $'^\tstate = running$'
}

case "${1:-status}" in
    status)
        if is_running; then
            print "on"
        else
            print "off"
        fi
        ;;
    start)
        if [[ ! -f "${support_dir}/config.json" || ! -f "${plist}" ]]; then
            print -u2 "桥接尚未安装或配置"
            exit 1
        fi
        /bin/launchctl enable "${service}"
        if ! is_loaded; then
            /bin/launchctl bootstrap "${domain}" "${plist}"
        fi
        /bin/launchctl kickstart -k "${service}"
        for _attempt in {1..50}; do
            is_running && break
            /bin/sleep 0.1
        done
        if is_running; then
            print "on"
        else
            print -u2 "无法启动飞书桥接服务，请运行诊断"
            exit 1
        fi
        ;;
    stop)
        if is_loaded; then
            /bin/launchctl bootout "${service}"
        fi
        /bin/launchctl disable "${service}"
        for _attempt in {1..50}; do
            ! is_loaded && break
            /bin/sleep 0.1
        done
        if is_loaded; then
            print -u2 "无法关闭飞书桥接服务"
            exit 1
        fi
        print "off"
        ;;
    restart)
        if is_loaded; then
            /bin/launchctl bootout "${service}"
        fi
        for _attempt in {1..100}; do
            ! is_loaded && break
            /bin/sleep 0.1
        done
        /bin/launchctl enable "${service}"
        /bin/launchctl bootstrap "${domain}" "${plist}"
        /bin/launchctl kickstart -k "${service}"
        print "on"
        ;;
    *)
        print -u2 "用法：${0:t} start|stop|restart|status"
        exit 2
        ;;
esac
