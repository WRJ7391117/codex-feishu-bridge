# PromLight 多用户、多灯、按 Task 提示

状态：桌面路径实现依据；移动 BLE 路径受真实协议与真机生命周期 Gate 约束。

## 产品边界

固定关系是：

```text
FeishuUser(open_id) <-> PromLight <-> TaskSubscription
```

Codex 仍运行在共享 Mac。飞书只提供身份、配置、通知和移动入口。手机或 Pad 只是一次配对或重连时的 BLE relay，不是业务绑定对象；用户更换终端后，灯归属和 Task 订阅仍由同一个飞书 `open_id` 保持。

桥接拥有工程渠道逻辑：飞书身份、允许访问的项目、每灯 Task 白名单、Task 状态聚合、菜单/卡片和通知路由。Ori Hub 只保留未来设备侧兼容语义：stable device ID、Capability、Adapter、command、observe/read-back、verify、设备事件、离线、超时和结果未知。当前 Hub 是 V1 Frozen / Maintenance & Home-Mind Integration Support；本功能不是正式 Hub 集成，不解锁 Hub V2/V3、额外真实设备、自动发现/重绑定或生产发布。

## 菜单与权限

飞书只新增一个一级菜单“提示灯”，下设两个二级入口：

- “我的提示灯”：查看已由 Mac App 绑定的灯，并管理重命名、默认灯、在线状态和每灯独立的关注 Task；不提供手机/Pad 或本地硬件配对入口。
- “灯光状态说明”：完整说明灯效、聚合优先级和离线语义。

菜单和卡片事件只使用当次实际操作者的 `operator_id`，缺失时拒绝状态变更；普通消息事件只使用其 `sender_id`。普通用户只能管理自己拥有的灯；Task 列表只来自该用户的 `allowed_projects`，保存前再次校验 Task 仍未归档且仍有权限。同一个 Task 可以被多名用户关注，一名用户可以有多盏灯，每盏灯有独立白名单。同一盏灯只有一个 `active_relay`。

## 灯效与聚合

只有显式关注的 Task 参与聚合：

1. 红灯闪烁：存在真实、明确的失败事件。
2. 黄灯闪烁：存在需要该用户介入的人工门。
3. 黄灯常亮：至少一个 Task 正在运行。
4. 绿灯常亮：关注 Task 全部完成或空闲。

卡片内以“灯光对应的事件说明”为标题，固定使用以下用户说明：

- 绿灯常亮：已完成，当前无需处理。
- 黄灯常亮：正在处理中。
- 黄灯闪烁：需要你处理。
- 红灯闪烁：执行出错，请查看 Task。

桥接拥有的运行会产生明确的 running、human gate 和 terminal 事件。Codex Desktop rollout 的 `task_failed`/`turn_aborted` 是明确失败；不能确认的异常不得推断为红灯。外部 Desktop 运行若没有可识别的人工门事件，维持“运行中”，不猜测黄闪。

灯离线时保留最后逻辑状态，并明确显示“提示灯离线”。命令发送成功不是灯效成功；PromLight 返回设备 ACK 时记录为 `acknowledged`，仍不等于独立灯效 read-back。`no-ack`、`no-device`、超时或网络错误记录为离线/unknown，并由单一合并 worker 按有界退避重试；不确定 Task 状态不会转换为绿色命令。

## 桌面 relay

本机 PromLight `0.2.3` 暴露只监听本机的 HTTP API：

- `GET http://127.0.0.1:7800/api/status`：发现在线灯。
- `POST http://127.0.0.1:7800/api/command`：使用明确的设备 reference 和北向灯命令。

桥接使用 opaque lamp ID 作为业务主键；PromLight 的本地 device reference 仅保存在权限为 `0600` 的 `state.json`，不进入仓库、日志或飞书卡片。灯效命令为：

- idle: `led green on --only`
- running: `led yellow on --only`
- human gate: `led yellow blink --only`
- error: `led red blink --only`

Bridge App 负责发现本机灯并把灯归属到一个已授权飞书用户。飞书卡片继续负责重命名、默认灯、Task 白名单、解绑和状态查看。

当前已验证组合为 PromLight 硬件（设备报告版本 `0.1.3`、release `19`）与 PromLight macOS App `0.2.3`，连接通道为 Mac BLE/HID。Bridge App 首页直接展示多灯发现、选择用户、命名和绑定流程，并显示连接设备实际报告的产品与版本。其他硬件型号或版本未完成真实验证前，不宣称兼容。

PromLight App 当前是独立的第三方签名程序，不包含在 Bridge 公共安装包中。其他 Mac 必须另行安装兼容的 PromLight App；在取得可再分发的软件包、许可和 Universal 构建之前，Bridge 不复制或重新签名该程序，也不宣称提示灯驱动已经一键安装。

## 移动 BLE 可行性与 Gate

飞书官方文档确认，小程序 Android/iOS `V3.25.0+`、HarmonyOS `V7.35.0+` 支持 `openBluetoothAdapter`、扫描、连接、服务/特征发现、`writeBLECharacteristicValue` 和读取；PC 不支持。官方没有单列 iPad，需以真实 iPad 飞书客户端验收。

官方运行机制同时说明：移动端小程序进入后台后一般最多保留约 5 分钟，也可能因系统资源或 iOS 内存告警提前销毁。因此小程序可承担用户在前台完成的首次配对、换设备、重连和物理确认，不能作为持续 Task 提醒 relay。

PromLight v2 的真实 BLE GATT service UUID、characteristic UUID、分包、ACK/read-back 和重连协议尚未从本机已授权资料或官方上游得到。本机实现使用 BLE HID/本地 daemon；公开同名仓库提供的 5E5E/HID 资料不能替代真实 GATT 证据。因此本轮不实现或宣称手机真机 BLE 闭环，飞书卡片也不显示移动配对入口。

参考：

- [飞书蓝牙接入流程](https://open.feishu.cn/document/client-docs/gadget/-web-app-api/device/bluetooth/bluetooth-api-guide)
- [writeBLECharacteristicValue](https://open.feishu.cn/document/uYjL24iN/ucTOxYjL3kTM24yN5EjN)
- [小程序运行机制](https://open.feishu.cn/document/client-docs/gadget/gadget-operation/operating-mechanism)
- [公开同名 PromLight 仓库](https://github.com/piaomiaoguying/PromLight)

## 收口与恢复

灯归属、默认灯、每灯 Task 白名单、最后逻辑状态和投递语义存入现有原子化 `state.json`，重启后恢复。取消订阅、Task 归档、权限撤销或解绑时立即停止后续 Task 驱动；解绑或撤权只有在 idle 命令收到设备 ACK 后才删除绑定，离线时保留“解绑待收口”以便恢复重试。最后一个订阅被移除时收口到 idle 后不再接收 Task 事件。实现复用现有 2 秒 rollout 事件观察节拍，不新增 10/30 分钟空轮询或云服务。
