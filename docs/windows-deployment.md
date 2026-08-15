# Windows LEAN/QMT 部署

本项目的部署目标是 Windows x64 宿主上的 Docker Desktop Linux 容器：

```text
大 QMT（用户手工登录、手工运行 Gateway 策略）
  ↕ host.docker.internal:17890
官方默认 LEAN Engine 镜像 + 本地 QuantConnect.Brokerages.Qmt.dll
  ↑
lean-cli live deploy --environment live-qmt
```

QMT 是 Brokerage，China A-share 是 LEAN market。策略使用：

```python
self.add_equity("600000", Resolution.MINUTE, market="china")
```

## 固定目录

```text
C:\Users\nemo\lean\Lean                  LEAN qmt 分支
C:\Users\nemo\lean\Lean.Brokerages.QMT  本仓库
C:\Users\nemo\lean\lean-cli              lean-cli qmt 分支
C:\Users\nemo\lean_project               策略根目录
```

QMT 使用 Windows 的 .NET 10 SDK 编译。目标框架和模块版本从默认 Engine 镜像标签
动态读取，不把版本写死在 Makefile。

## 一次性安装

从 Mac 的本仓库执行：

```bash
make install-windows
```

安装脚本是幂等的，完成三件事：

1. 验证 lean-cli `qmt` 分支能够识别 `QMT` Brokerage 和 data queue；
2. 恢复 `quantconnect/lean:latest` 和 `quantconnect/research:latest` 默认镜像；
3. 从现有 `lean.json` 生成隔离的 `C:\Users\nemo\lean_project\lean-qmt.json`，
   加入 `live-qmt` environment，并强制 `qmt-trading-enabled=false`。

账号只存在 Windows 本地 `lean-qmt.json`，不会写入 Git。原 `lean.json` 不会被覆盖。
QMT DLL 直接挂载到默认 Engine 容器，不构建自定义镜像，也不生成 NuGet 包。

## 编译和发布本地 Brokerage

首次运行 Docker Desktop 时，需要用户本人在 RDP 会话中接受 Docker Desktop
许可条款。自动化不能代替用户接受条款。Docker daemon ready 后执行：

```bash
make package-windows
```

命令依次执行：

```text
Git push/fetch/fast-forward
→ Windows Python/.NET 测试
→ dotnet build
→ 读取 quantconnect/lean:latest 的 lean_version/target_framework
→ %USERPROFILE%\.lean\modules\QmtBrokerage\<lean_version>\<target_framework>
```

## 真实只读全链路验证

```bash
make e2e-brokerage-readonly
make e2e-readonly
make test-live
```

`make e2e-brokerage-readonly` 直接构造真实 `QmtGatewayClient` 和
`QmtBrokerage`，验证账号握手、资金、持仓、未完成委托、日线/分钟历史、订阅、
退订和主动断开后的连接重建。它不验证自动故障恢复，并要求 Gateway 与 LEAN
两端交易开关均为关闭状态，不调用下单接口。

`make e2e-readonly` 先运行 Brokerage E2E，再运行下面的完整 LEAN live smoke。
精简证据由 Windows Nginx 暴露：

```text
http://192.168.50.135:8000/e2e/qmt-readonly-e2e.log
```

该命令要求用户已在大 QMT 中手工运行真实 Gateway，随后执行：

```text
lean-cli
→ quantconnect/lean:latest
→ 挂载版本匹配的本地 QMT DLL
→ Composer/QmtBrokerageFactory
→ host.docker.internal:17890
→ hello/query_account/query_positions/query_orders/query_history/subscribe
→ AddEquity(... market="china")
→ 真实分钟行情
→ clean exit
```

`qmt-trading-enabled=false`，测试不下单，也不启动、停止或重启 QMT 客户端。

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
  --no-update `
  --extra-docker-config <QMT-DLL-volume> `
  --detach
```

先验证资金、持仓、未完成委托和实时行情。只有模拟账户交易闭环验收完成，并由用户
明确决定后，才能同时打开 QMT 和 LEAN 两端交易开关。

## 常规验证与日志

```bash
make test             # Windows Python tests、.NET build、NUnit tests
make install-windows  # 可重复执行，验证安装幂等性
make package-windows  # Windows 编译、测试并发布版本化本地 DLL
make e2e-brokerage-readonly # 真实 QMT Brokerage 非交易 E2E
make e2e-readonly     # Brokerage E2E + 完整 LEAN live smoke
make test-live        # 真实 QMT 只读完整部署 smoke
```

日志保存在：

```text
.test-logs/windows-test.log
.test-logs/windows-test-full.log
.test-logs/windows-deployment-install.log
.test-logs/windows-deployment-test.log
.test-logs/windows-live-test.log
```
