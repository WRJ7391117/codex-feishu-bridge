# DeepOri Bridge for Windows 开发与验收工作流

本流程用于 Windows 真机开发，不改变现有 Mac Bridge，不直接修改飞书生产版本。任务协作遵循单写者原则，所有人工 Gate 由产品总控统一向用户提出。

## 固定角色

1. **产品总控与人工 Gate**：拆分批次、维护验收标准、汇总证据、向用户提出真正需要人工判断的 Gate，并接收明确的提交、推送或发布授权。
2. **Windows 主实现**：唯一代码写者。只修改批准范围内的 Windows 实现、测试和文档；在没有人工 Gate 时持续完成构建、测试与窗口验收，不直接向用户重复索要确认。
3. **安全审计与兼容性**：严格只读。检查 Windows/macOS 隔离、凭据处理、owner-only 与本机人工确认边界、向后兼容及敏感文件风险；不得编辑工作树。
4. **发布与分发验证**：默认只读。核对候选 SHA、packaged 构建、安装/卸载、签名与分发证据；只有在总控取得精确发布授权后才能执行相应写操作。

## 状态机

`BACKLOG → IMPLEMENTING → READY_FOR_AUDIT → PASS/BLOCK → READY_FOR_RELEASE → HUMAN_GATE → RELEASED`

- `BACKLOG`：总控已记录但尚未分配唯一写者。
- `IMPLEMENTING`：Windows 主实现正在编辑和验证；审计与发布角色保持只读。
- `READY_FOR_AUDIT`：实现冻结，提供完整差异和证据，等待独立审计。
- `PASS/BLOCK`：审计明确通过，或给出可复现阻断项并退回实现。
- `READY_FOR_RELEASE`：审计通过且候选内容、构建产物和签名范围均已固定。
- `HUMAN_GATE`：只有总控可以向用户请求提交、推送、签名、发布或设备肉眼确认。
- `RELEASED`：精确获批的候选已完成发布，并记录最终 SHA 与产物哈希。

## 批次交接证据

每次从 `IMPLEMENTING` 转入 `READY_FOR_AUDIT`，Windows 主实现必须向总控提交以下结构化证据：

1. 批次编号与目标。
2. Git 基线：`HEAD`、`origin/main`、ahead/behind、工作树初始状态。
3. 精确范围：新增、修改、删除文件清单。
4. 候选差异：`git diff --stat`、`git diff --check`、候选 diff 哈希。
5. 构建：配置、架构、SDK、命令、0/非 0 警告和错误。
6. 自动化测试：协议测试、集成测试、sidecar Go 测试及各自通过数量。
7. 运行证据：实际进程路径、PID、主窗口句柄、可见性、响应状态、DPI 与窗口尺寸。
8. UI 证据：主页与新增页面可访问，返回导航正常；wide/medium/compact 布局；键盘焦点、滚动起点与必要截图。
9. 安全证据：凭据只进入 Windows Credential Locker；没有提交凭据、用户 ID、`config.json`、`state.json`、数据库或日志；没有扩大到所有项目；Mac 与生产飞书配置未改。
10. 剩余 Gate：只列真正需要人工肉眼、硬件、签名或外部平台权限的项目。
11. 最终状态：`READY_FOR_AUDIT`，不得自行标记 `PASS` 或 `RELEASED`。

## 构建与窗口验收

标准候选使用 packaged WinUI 3 Release x64 构建：

```powershell
$env:NUGET_PACKAGES = 'D:\Evolution\Caches\NuGet'
dotnet build windows\DeepOriBridge\DeepOriBridge.csproj -c Release -p:Platform=x64
```

构建成功不等于验收完成。主实现还必须从本次 Release 输出启动应用，确认出现可见、可响应的主窗口，通过 UI Automation 或等价方式遍历本批页面与返回路径，并在交接审计时保留最终候选应用窗口运行。旧进程必须先按精确可执行文件路径重新发现后再关闭，不能依赖历史 PID。

## 权限与禁止项

- 没有总控转交的用户明确授权，不得 commit、push、创建发布、签名或上传产物。
- 审计角色不得写文件、格式化代码、暂存变更或修复问题。
- 发布角色只处理总控固定的精确 SHA 与产物，不得把“发布”解释为修改实现。
- 不提交任何凭证、用户 ID、`config.json`、`state.json`、数据库、日志或包含这些内容的截图。
- Windows 实现不得改写 Mac Bridge 行为，不得声称 Windows PromLight 可用，除非有真机设备和协议证据；每次硬件写入仍需人工确认物理灯效。
- Windows 后台/托盘/自启动目前是明确未完成能力；在实现并验收前，UI 必须说明关闭窗口会停止会话，不得沿用 Mac 的“关窗仍后台运行”文案。
- 首次连接和路由不得默认授权所有项目。任何 Task 创建、Bot 回复或 owner 绑定都必须遵守现有本机人工 Gate。
