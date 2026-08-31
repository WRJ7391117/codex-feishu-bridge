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

autostart_enabled() {
    ! /bin/launchctl print-disabled "${domain}" 2>/dev/null \
        | /usr/bin/grep -Fq "\"${label}\" => disabled"
}

start_service() {
    if [[ ! -f "${support_dir}/config.json" || ! -f "${plist}" ]]; then
        print -u2 "桥接尚未安装或配置"
        return 1
    fi
    local preserve_disabled=no
    if ! autostart_enabled; then
        preserve_disabled=yes
    fi
    /bin/launchctl enable "${service}" || return 1
    if ! is_loaded; then
        /bin/launchctl bootstrap "${domain}" "${plist}" || return 1
    fi
    /bin/launchctl kickstart -k "${service}" || return 1
    local started=no
    for _attempt in {1..50}; do
        if is_running; then
            started=yes
            break
        fi
        /bin/sleep 0.1
    done
    if [[ "${preserve_disabled}" == yes ]]; then
        /bin/launchctl disable "${service}" || return 1
    fi
    [[ "${started}" == yes ]] && return 0
    print -u2 "无法启动飞书桥接服务，请运行诊断"
    return 1
}

stop_service() {
    if is_loaded; then
        /bin/launchctl bootout "${service}" || return 1
    fi
    for _attempt in {1..100}; do
        ! is_loaded && return 0
        /bin/sleep 0.1
    done
    print -u2 "无法关闭飞书桥接服务"
    return 1
}

case "${1:-status}" in
    status)
        if is_running; then
            print "on"
        else
            print "off"
        fi
        ;;
    autostart-status)
        if autostart_enabled; then
            print "on"
        else
            print "off"
        fi
        ;;
    enable-autostart)
        /bin/launchctl enable "${service}" || exit 1
        print "on"
        ;;
    disable-autostart)
        /bin/launchctl disable "${service}" || exit 1
        print "off"
        ;;
    start)
        start_service || exit 1
        print "on"
        ;;
    stop)
        stop_service || exit 1
        print "off"
        ;;
    restart)
        stop_service || exit 1
        start_service || exit 1
        print "on"
        ;;
    *)
        print -u2 "用法：${0:t} start|stop|restart|status|autostart-status|enable-autostart|disable-autostart"
        exit 2
        ;;
esac
