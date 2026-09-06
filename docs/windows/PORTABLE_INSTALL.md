# Windows 便携测试版

此说明用于正在验证的未签名测试版，不代表已发布或已完成全新电脑验收。

## 安装和首次使用

1. 仅从 `WRJ7391117/codex-feishu-bridge` 的 GitHub Releases 获取明确标为
   Windows x64 unsigned preview 的 ZIP。不要从聊天转发文件判断其来源。
2. 使用 `Get-FileHash -Algorithm SHA256 <下载的ZIP路径>` 与该 Release 的
   SHA256SUMS.txt 比对。校验值检测损坏；不能代替可信发布者签名。
3. 解压整个文件夹到本地磁盘，勿只复制 EXE，勿在 ZIP 内运行。不要往程序
   文件夹加入配置、日志或插件：完整性校验会拒绝清单外文件。
4. 运行 DeepOriBridge.exe。无需 Git、Go、Visual Studio 或开发者模式。
   Windows 可能警告发布者未知。不要关闭 Defender、SmartScreen 或组织安全策略；
   如果组织阻止运行，请联系管理员，勿绕过策略。
5. 首次启动阅读并确认信任提示。默认操作为退出；只有明确同意才创建桥接服务。
6. 安装并登录你自己的 Codex Desktop。在 Bridge 中填写自己的飞书应用信息；
   App Secret 只填在 App 的密码输入框，勿发到 Codex、飞书或终端。
7. 按向导配置应用、绑定使用者并明确授权项目，验证文字、截图和文件往返。
   提示灯是可选硬件；多灯需分别指定设备和使用者。

## 安全边界与数据

本版没有 Windows 发布者签名，属于用户自行确认来源的测试版。完整性、规范路径、
当前进程及窗口校验仍启用，但不抵御已经控制同一 Windows 账户的攻击者。
信任确认绑定文件指纹和所在目录。更新、回退或移动目录都需要重新确认。

程序文件可移动，个人数据并不放在 ZIP 内。数据位于当前用户的
`%LOCALAPPDATA%\DeepOriBridge\Portable-Unsigned-Isolated-v1`；凭证位于当前用户
Windows Credential Locker 的独立 `DeepOriBridge.Windows.Portable.Unsigned.Isolated.v1`
命名空间。不自动导入开发版或正式版凭证、队列、项目授权或提示灯绑定。

## 更新、回退、卸载

正常关闭 App 后，将新版本解压到新的目录，不覆盖正在运行的程序。确认旧进程已退出，
再启动新版本。不要同时运行两个便携版本。回退同样使用独立目录并重新确认信任。
测试版尚无跨数据格式降级保证，遇到不兼容应停止，不要删除数据强行启动。

卸载程序只需在正常退出后删除程序文件夹。个人数据和凭证不会自动删除；如需彻底
清除，应先在 App 解除授权，再明确删除便携版专属数据，勿删除开发版或其他应用数据。

维护者仍须验证：无预装开发环境的干净 Windows 11 x64、真实 Credential Locker、
文字/图片/文件往返、升级回退、崩溃重启、两个提示灯及两个授权使用者。
