# AI 上下文同步：Windows 托盘管理器

[English](README.md) · [Commander 定制版](https://github.com/SANG4242/DesktopCommanderMCP/tree/custom/chatgpt-ui) · [项目背景](https://sang4242.pages.dev/zh/projects/desktop-commander/)

这个 Windows 托盘程序用于管理我接入 ChatGPT 的本地服务：分别启停两路 OpenAI Tunnel Client，查看连接与健康状态，切换日志，并通过命令行控制客户端供脚本调用。

开发它的起点是在网页 ChatGPT 中继续使用本机已有的资料和项目。文件仍在原来的目录中，管理器负责服务接入与日常运行。

## 组成

```text
ChatGPT
  → OpenAI Secure MCP Tunnel
  → 本机 tunnel-client
       ├─ Desktop Commander 定制版 · 8081
       └─ Filesystem MCP · 8080，可选回退
            ↑
     Windows 托盘 + AIContextSyncCtl
```

| 组件 | 来源 |
| --- | --- |
| 本仓库的托盘、服务管理和 CLI | 我使用 C#/.NET 实现，开发中使用 AI 协助，并结合本地调试。 |
| Terminal／Diff 界面和文件操作扩展 | 我对 [Desktop Commander](https://github.com/SANG4242/DesktopCommanderMCP/tree/custom/chatgpt-ui) 的定制；基础服务和已有 File Preview 来自上游。 |
| Secure MCP Tunnel 与 tunnel-client | OpenAI 官方基础设施和客户端，通过账号中的 Tunnel 设置获取。 |
| Filesystem MCP | 官方 `@modelcontextprotocol/server-filesystem` 包。 |

## 日常操作

- 单独启动、停止或重启服务，保留另一服务的运行状态。
- 选择参与监测和批量操作的服务。
- 查看本地健康／就绪、MCP 进程与控制面可达状态。
- 打开对应的官方 Tunnel 管理页，或切换查看各路日志。
- 设置登录自启、托盘启动时自动连接和通知。
- 使用 `AIContextSyncCtl` 供脚本查询、启停服务。

### 托盘菜单

![已选 Commander 服务的托盘菜单](docs/screenshots/tray-service-menu.png)

图中只启用了 Commander 的监测，Filesystem 未参与。

### 从管理器打开官方 Tunnel 界面

![官方 Tunnel Client 概览](docs/screenshots/tunnel-overview.png)

![官方 Tunnel Client 运行日志](docs/screenshots/tunnel-runtime-logs.png)

这两张浏览器页面由官方 Tunnel Client 提供，托盘提供打开入口。实例、隧道等标识已遮盖。

## 环境要求

- Windows 10／11 x64；开发和日常使用环境为 Windows 11。
- 构建使用 .NET 9 SDK；框架依赖版程序需要 .NET 9 Desktop Runtime。
- Node.js 22、Commander 源码构建，以及官方 Tunnel Client `0.0.14` 和配套文件。
- 账号或工作区具有所需的 OpenAI Tunnel、ChatGPT MCP／App 功能。

仓库提供管理器源码和 Profile 示例。认证信息、官方可执行文件、Node 依赖和完整 Commander 源码分别配置；依赖版本见 `tools.dependencies.json`。

## 安装与配置

### 1. 构建管理器

```powershell
git clone https://github.com/SANG4242/ai-context-sync.git
cd ai-context-sync
dotnet publish tray/AIContextSyncTray.csproj -c Release -r win-x64 --self-contained false -o artifacts/win-x64
```

发布目标会同时把 `AIContextSyncCtl.exe` 输出到该目录。使用 Release 压缩包时，将文件解压到仓库下的 `artifacts/win-x64/`。

### 2. 准备本地依赖

```powershell
git clone --branch custom/chatgpt-ui https://github.com/SANG4242/DesktopCommanderMCP.git tools/desktop-commander
npm --prefix tools/desktop-commander ci --ignore-scripts
npm --prefix tools/desktop-commander run build
npm install --prefix tools/filesystem-mcp --save-exact --ignore-scripts @modelcontextprotocol/server-filesystem@2026.8.31
```

将官方 Tunnel Client 的完整配套文件放到 `tools/tunnel-client/v0.0.14/bin/`，包含 `tunnel-client.exe`、配套可执行文件和许可说明。也可以用 `AI_CONTEXT_SYNC_TUNNEL_CLIENT` 指定客户端绝对路径。

### 3. 配置 Profile

复制两份模板。实际 `.yaml` 配置已经由 Git 忽略。

```powershell
Copy-Item config/tunnel-client/local-desktop-commander.example.yaml config/tunnel-client/local-desktop-commander.yaml
Copy-Item config/tunnel-client/local-filesystem.example.yaml config/tunnel-client/local-filesystem.yaml
```

在自己的 OpenAI 工作区创建 Tunnel，并填入对应 ID。启用两路服务时分别使用独立 Tunnel。`api_key` 使用 `env:变量名` 或 `file:C:/密钥文件绝对路径` 引用，密钥本体保存在仓库及文件授权范围之外。

启动托盘前，在其环境中设置被引用的变量，或准备本地密钥文件。托盘解析引用进行控制面探测，传给 Tunnel Client 的进程参数仍是引用。

模板中的命令使用 YAML 单引号包裹。将 Filesystem 示例中的 `C:/mcp-workspace` 改为自己明确授权的现有目录；命令内部含空格的路径再用双引号包裹。管理器读取这些简单 Profile 字段，复杂 YAML 配置需另行适配。

如需出站代理，在 `control_plane` 下添加 `http_proxy: http://127.0.0.1:端口`。未设置时无需本地代理。健康／管理端点保持监听 `127.0.0.1` 和对应端口。

### 4. 启动与连接

```powershell
$env:AI_CONTEXT_SYNC_ROOT = (Get-Location).Path
& ./artifacts/win-x64/AIContextSyncTray.exe
```

在“设置 → 监测的 MCP”中选择服务，再手动启动。公开版默认由用户选择是否启用自动连接。在 ChatGPT 中按照自己工作区的 Tunnel 设置建立对应连接。

界面目前为中文。`AI_CONTEXT_SYNC_ROOT` 可覆盖项目根目录，`AI_CONTEXT_SYNC_DATA_DIR` 可覆盖运行数据目录；默认运行数据位于仓库之外的 `%LOCALAPPDATA%/AIContextSync`。服务健康端口固定为 8080／8081。

## 命令行控制

托盘需要已在同一 Windows 用户下运行。

```powershell
./artifacts/win-x64/AIContextSyncCtl.exe service status all --json
./artifacts/win-x64/AIContextSyncCtl.exe service restart commander --json
./artifacts/win-x64/AIContextSyncCtl.exe service stop filesystem
```

支持 `status`、`start`、`stop`、`restart`；目标为 `commander`、`filesystem` 或 `all`。

## 检查与运行说明

```powershell
dotnet build tray/AIContextSyncTray.csproj -c Release
dotnet run --project tests/ManagerChecks/ManagerChecks.csproj -c Release
```

离线检查验证路径覆盖、Profile 解析和启动参数，不启动真实 Tunnel、不连接 OpenAI、不改 Windows 自启设置。截图展示日常使用版本；公开配置适配仍需在使用者自己的工作区完成端到端验证。

- 控制面可达与本地就绪分开显示。控制面检查失败可能与 API 权限或网络有关，本地 MCP 此时仍可能正常就绪。
- 管理器不提供沙箱。远程执行命令前，按最小范围授权目录，并阅读 Commander／上游安全说明。
- 同一用户的正常安装共用命名管道和单实例锁。测试时勿另起一份托盘连接生产服务。
- 日志可能包含路径、参数和工具结果，共享前检查正文。日志轮换会把旧日志或过大的日志移入 Windows 回收站。

## 许可证

管理器源码采用 [MIT](LICENSE)，版权归 Zhi Sang。Desktop Commander、Filesystem MCP、.NET 和 OpenAI Tunnel 组件分别遵循各自许可证；本仓库通过官方来源获取 Tunnel 二进制文件。
