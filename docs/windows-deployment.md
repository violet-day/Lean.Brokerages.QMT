# Windows LEAN/QMT 部署

本项目的部署目标是 Windows x64 宿主上的 Docker Desktop Linux 容器：

```text
大 QMT（用户手工登录、手工运行 Gateway 策略）
  ↕ host.docker.internal:17890
自定义 LEAN Engine 镜像（包含 QuantConnect.Brokerages.Qmt.dll）
  ↑
lean-cli live deploy --environment live-qmt
```

QMT 是 Brokerage，China A-share 是 LEAN market。策略使用：

```python
self.add_equity("600000", Resolution.TICK, market="china")
```

## 固定目录

```text
C:\Users\nemo\lean-net10\Lean                  LEAN d72852f25
C:\Users\nemo\lean-net10\Lean.Brokerages.QMT  本仓库
C:\Users\nemo\lean\lean-cli                    lean-cli 源码
C:\Users\nemo\lean_project                      策略根目录
```

Windows 的 `C:\Users\nemo\.dotnet\dotnet.exe` 是权威 .NET 10。系统 PATH
中的旧 SDK 不参与构建。

## 一次性安装

从 Mac 的本仓库执行：

```bash
make install-windows
```

安装脚本是幂等的，完成三件事：

1. 给 Windows LEAN Launcher 增加指向 sibling QMT csproj 的
   `ProjectReference`；
2. 给 Windows lean-cli 增加 `modules-local.json` overlay，使官方模块清单刷新后
   仍能识别 `QMT` Brokerage 和 data queue；
3. 从现有 `lean.json` 生成隔离的 `C:\Users\nemo\lean_project\lean-qmt.json`，
   加入 `live-qmt` environment，并强制 `qmt-trading-enabled=false`。

账号只存在 Windows 本地 `lean-qmt.json`，不会写入 Git。原 `lean.json` 不会被覆盖。
NuGet 不是这条部署链的前置条件；Launcher 的 ProjectReference 会把插件 DLL 和依赖
直接带入自定义镜像。

## 构建镜像

首次运行 Docker Desktop 时，需要用户本人在 RDP 会话中接受 Docker Desktop
许可条款。自动化不能代替用户接受条款。Docker daemon ready 后执行：

```bash
make image
```

默认镜像：

```text
lean-cli/engine:qmt-20260813-d72852f25-worktree
```

脚本在 Windows 使用 .NET 10 构建 Launcher，断言 Launcher output 和
`deps.json` 都包含 `QuantConnect.Brokerages.Qmt`，构建镜像后再从容器内检查 DLL、
依赖清单和 .NET runtime。

可用环境变量覆盖 tag：

```bash
QMT_IMAGE_TAG=qmt-YYYYMMDD-<lean-sha>-<qmt-sha> make image
```

## fake-only 全链路验证

```bash
make test-deployment
```

该命令只启动 `0.0.0.0:17891` 的 standalone fake Gateway，使用假账号
`deployment-test`，随后执行真实：

```text
lean-cli
→ 自定义镜像
→ Composer/QmtBrokerageFactory
→ host.docker.internal:17891
→ hello/query_account/query_positions/query_orders/subscribe
→ AddEquity(... market="china")
→ fake quote
→ clean exit
```

脚本会拒绝任何 `place_order` 或 `cancel_order`。它不连接真实 QMT、不读取真实账户、
不下单，也不启动、停止或重启 QMT 客户端。

## 只读真实部署

真实 Gateway 必须由用户在大 QMT 中手工启动。Docker 容器不能访问只绑定
`127.0.0.1` 的 Gateway，因此 Windows 本地 `qmt_local_config.py` 需要：

```python
GATEWAY_BIND_HOST = "0.0.0.0"
GATEWAY_ALLOW_REMOTE_CLIENTS = True
TRADING_ENABLED = False
```

协议当前是无 TLS、无认证的明文 TCP。必须用 Windows 防火墙只允许 Docker Desktop
内部网络访问 17890，绝不能开放给局域网或公网。LEAN 端也必须保持：

```json
"qmt-trading-enabled": "false"
```

准备好 Gateway 后，命令形式为：

```powershell
lean live deploy C:\Users\nemo\lean_project\<project> `
  --lean-config C:\Users\nemo\lean_project\lean-qmt.json `
  --environment live-qmt `
  --image lean-cli/engine:qmt-20260813-d72852f25-worktree `
  --no-update `
  --detach
```

先验证资金、持仓、未完成委托和实时行情。只有模拟账户交易闭环验收完成，并由用户
明确决定后，才能同时打开 QMT 和 LEAN 两端交易开关。

## 常规验证与日志

```bash
make test             # Windows Python 14/14、.NET build、NUnit 51/51
make install-windows  # 可重复执行，验证安装幂等性
make image            # Windows 自定义镜像
make test-deployment  # fake-only 完整部署 smoke
```

日志保存在：

```text
.test-logs/windows-test.log
.test-logs/windows-deployment-install.log
.test-logs/windows-deployment-image.log
.test-logs/windows-deployment-test.log
.test-logs/windows-launcher-build.log
```
