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

## 编译和发布本地 Brokerage

首次运行 Docker Desktop 时，需要用户本人在 RDP 会话中接受 Docker Desktop
许可条款。自动化不能代替用户接受条款。Docker daemon ready 后执行：

```bash
make test
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
make test-readonly
make test-smoke
```

`make test-readonly` 直接构造真实 `QmtGatewayClient` 和 `QmtBrokerage`，
验证账号握手、资金、持仓、未完成委托、日线/分钟历史、订阅、退订和主动断开后的
连接重建。`make test-smoke` 独立运行完整 LEAN live smoke。两者都不验证自动故障恢复，
测试代码只执行查询和行情订阅，不调用下单接口。
精简证据由 Windows Nginx 暴露：

```text
http://192.168.50.135:8000/e2e/qmt-readonly-e2e.log
http://192.168.50.135:8000/e2e/test-smoke.log
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

测试不下单，也不启动、停止或重启 QMT 客户端。

## 只读真实部署

真实 Gateway 必须由用户在大 QMT 中手工启动。Docker 容器不能访问只绑定
`127.0.0.1` 的 Gateway，因此 Windows 本地 `qmt_local_config.py` 需要：

```python
GATEWAY_BIND_HOST = "0.0.0.0"
GATEWAY_ALLOW_REMOTE_CLIENTS = True
```

协议当前是无 TLS、无认证的明文 TCP。必须用 Windows 防火墙只允许 Docker Desktop
内部网络访问 17890，绝不能开放给局域网或公网。

准备好 Gateway 后，命令形式为：

```powershell
lean live deploy C:\Users\nemo\lean_project\<project> `
  --lean-config C:\Users\nemo\lean_project\lean-qmt.json `
  --environment live-qmt `
  --no-update `
  --extra-docker-config <QMT-DLL-volume> `
  --detach
```

不需要在 `lean-qmt.json` 配置模拟/实盘或 MarketOrder style。Gateway hello 会从
当前运行的 QMT 终端自动返回账号属性：模拟终端使用最新价 `price_type=5`；实盘终端
使用 `five-level-immediate-or-cancel`，沪市/北市映射为 `42`、深市映射为 `47`。
QMT 模拟交易不支持原生股票市价类型 `42-48`。模拟账号在工作日 10:00–17:00 之外
会在调用 QMT 前明确拒单，避免 `passorder` 静默丢弃请求。

先验证资金、持仓、未完成委托和实时行情，再在确认当前 QMT 账号后显式运行交易测试。

## 常规验证与日志

日志由 Windows 原生 Nginx 直接暴露 `C:\Users\nemo\lean_logs`，通过
`QmtLiveLogs` 开机计划任务常驻，不依赖 Docker Desktop、WSL 或 LEAN 容器。

```bash
make sync-windows    # 仅通过 Git 同步已提交分支
make package-windows # 同步并确保当前代码对应的已验证 DLL 已发布
make test            # 同步、Windows 测试并发布版本化本地 DLL
make test-readonly   # 只跑真实 Brokerage 非交易 E2E
make test-smoke      # 只跑完整 LEAN live smoke
make test-trading
```

`make test-trading` 要求 Gateway `hello` 返回的账号与 `lean-qmt.json` 完全一致。
测试固定使用 `600000.SH`、数量 `100`。模拟交易时段内验证非成交限价单的提交、
`Submitted` 回调、撤单、`Canceled` 回调及最终订单查询；时段外验证 QMT 明确拒单。
时段外同时验证 `latest-price` MarketOrder 被拒绝。本地非法订单 case 不会发送到
QMT。`QmtTradingInventory` 类别中的 MarketOrder 成交 case 不属于默认
`test-trading`，因为每次执行会增加 100 股 T+0 持仓。每个 case 失败时均按唯一
client ID 查询并尝试撤销遗留委托。日志为：

```text
http://192.168.50.135:8000/e2e/test-trading.log
```

日志保存在：

```text
.test-logs/windows-test.log
.test-logs/windows-test-full.log
.test-logs/windows-package.log
.test-logs/windows-package-full.log
.test-logs/windows-deployment-test.log
.test-logs/windows-live-test.log
```
