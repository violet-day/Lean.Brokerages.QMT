# QMT Brokerage 开发与部署路线图

本文档追踪 `Lean.Brokerages.QMT` 从 Gateway/Brokerage MVP 到 `lean-cli`
部署的完成状态。QMT 是 Brokerage/交易执行端，目标市场是 China A-share
（上海、深圳、北京交易所）；两者不是同一个概念。

最后更新：2026-08-13

## 目标架构

```text
Windows x64 宿主机
├── 国金大 QMT（用户手工登录并运行策略）
│   └── QMT Python Gateway（QMT 内嵌 Python 3.6.8）
│       ├── 账户、持仓、委托查询
│       ├── China A-share 下单、撤单和行情订阅
│       └── 行情、委托、成交、账户、持仓回调
│
└── Docker Desktop（Linux/amd64，尚未完成）
    └── 自定义 LEAN Engine 镜像
        ├── LEAN 策略
        └── QuantConnect.Brokerages.Qmt.dll
            └── 通过 host.docker.internal 连接 QMT Gateway
```

订单事件闭环：

```text
LEAN PlaceOrder
→ QmtBrokerage
→ TCP/NDJSON Gateway
→ 大 QMT
→ order/deal callback
→ QMT Gateway
→ QmtBrokerage OnOrderEvent
→ LEAN 策略
```

## 固定版本与测试基线

- [x] Brokerage、Mac LEAN 和 Windows LEAN 对齐到 .NET 10。
- [x] Windows 安装并由测试脚本校验 .NET 10 SDK。
- [x] Windows 权威工作区固定为
  `C:\Users\nemo\lean-net10\Lean.Brokerages.QMT`。
- [x] Mac/Windows 离线测试环境固定 Python `3.11.13`。
- [x] QMT 策略/Gateway 代码保持定制 Python `3.6.8` 语法兼容。
- [x] `make test` 只在 Windows 执行 Python、C# 编译和 NUnit 测试。
- [x] 最新记录：Python 14/14、NUnit 51/51、C# build 0 errors。
- [x] Mac/Windows LEAN 固定为
  `d72852f25e81cf4505a9059fc037c7c49cd21825`。

完整 Windows 输出保存在 `.test-logs/windows-test.log`。自动测试只使用假 QMT
函数和本机回环 Fake Gateway，不连接真实 QMT，不读取真实账户，也不下单。

## MVP 已完成

### A. Gateway 协议与 C# 客户端

- [x] ADR 固定协议 v1：单 TCP 连接、UTF-8 NDJSON。
- [x] 定义 `protocol_version`、消息类型、request ID、operation、错误和 payload。
- [x] `hello` 校验账号并返回 Gateway 的 `trading_enabled` 状态。
- [x] 定义资金、持仓、委托查询消息。
- [x] 定义 Market/Limit 下单和撤单消息。
- [x] 定义行情订阅及以 `subscription_id` 退订。
- [x] 定义 quote/order/deal/position/account/connection 事件。
- [x] C# 客户端实现并发 request ID 关联、超时、reader loop、事件和断线通知。
- [x] Fake TCP Gateway 覆盖握手、乱序响应、事件、错误、超时和断线。
- [ ] 自动重连后恢复订阅和连接状态。

协议详见 `docs/adr/0001-qmt-gateway-protocol.md`。

### B. QMT Python Gateway

- [x] `qmt_gateway_entry.py` 作为只复制一次的稳定 QMT 入口。
- [x] 从 Windows Git 工作区直接加载 `lean_qmt_gateway.py`，不依赖 `importlib`。
- [x] TCP 线程只收发和排队；QMT `handlebar` 线程执行 QMT API。
- [x] 实现账号握手和默认关闭的交易开关。
- [x] 实现 ACCOUNT、POSITION、ORDER 查询及字段归一化。
- [x] 实现行情订阅、退订和 quote 事件。
- [x] 实现 place/cancel 协议和 QMT 参数映射。
- [x] 实现 order/deal/account/position/error callback 事件与结构化日志。
- [x] 缓存 request ID 响应，避免同一请求重复执行。
- [x] 默认只绑定 `127.0.0.1`；非回环绑定需要显式安全开关。
- [x] `stop` 关闭监听、连接和行情订阅。
- [ ] 在真实 QMT 中保存 Gateway 启动、查询、订阅及 callback 日志证据。
- [ ] 根据真实日志最终确认不同 QMT 版本的字段名和数值语义。
- [ ] 在模拟账户验证真实 `passorder`/`cancel` 调用及回报。

### C. C# Brokerage MVP

- [x] `QmtBrokerageModel`：现金账户、1 倍杠杆、China A-share Equity。
- [x] 支持 Market/Limit；`UpdateOrder` 明确返回不支持。
- [x] `QmtBrokerageFactory` 读取配置并注册共享 `IDataQueueHandler`。
- [x] 实现连接、断开和 Gateway 账号握手。
- [x] 实现 `GetCashBalance()`、`GetAccountHoldings()`、`GetOpenOrders()`。
- [x] 实现 `PlaceOrder()` 和 `CancelOrder()` 协议调用。
- [x] 实现上海、深圳、北京 QMT 证券代码转换。
- [x] 实现行情订阅/退订和 QMT tick 到 LEAN `Tick`。
- [x] 将 order/deal callback 转成 LEAN `OrderEvent`。
- [x] 本地与 Gateway 两个交易开关必须同时开启。
- [x] Brokerage 和 Gateway 自动测试全部使用 fake，不接真实账户。
- [ ] 启动时自动完成资金、持仓和未完成委托对账。
- [ ] 断线自动重连、恢复订阅、状态恢复和事件去重。
- [ ] 对 order/deal 重复或乱序回调增加持久化防护。
- [ ] 补齐生产手续费、交易时段、最小下单单位和涨跌停规则。

## 手工 QMT 运行步骤

1. 在 Mac 执行 `make sync-windows`。
2. Windows 创建
   `C:\Users\nemo\lean-net10\Lean.Brokerages.QMT\qmt_python\qmt_local_config.py`，
   来源为同目录的 `qmt_local_config.example.py`。
3. 填写 `ACCOUNT_ID`，保持 `TRADING_ENABLED = False`。
4. 在大 QMT 策略编辑器中新建/打开策略，把 `qmt_gateway_entry.py` 全文复制进去。
   QMT 的模型导入窗口只认打包格式，不要用它导入源码目录。
5. 用户手工选择账号并运行策略；自动化不得启动、停止或重启 QMT。
6. 检查 `[lean_qmt_gateway] server_started ... trading_enabled=False`。

入口每次启动都会读取工作区内最新 `lean_qmt_gateway.py`。后续同步代码后，只需由
用户手工重新运行已有策略，不需要再次复制入口。

## 安全边界

- [x] Python `TRADING_ENABLED` 默认 `False`。
- [x] LEAN `qmt-trading-enabled` 默认 `false`。
- [x] 任一开关关闭时，Brokerage 拒绝下单和撤单。
- [x] 自动测试永远不修改这两个真实配置，也不连接真实 Gateway。
- [x] Gateway 默认仅监听 `127.0.0.1:17890`。
- [ ] Docker 接入前配置受保护的非回环监听和最小范围 Windows 防火墙规则。
- [ ] 实盘启用前确认端口未暴露公网；v1 是无 TLS、无认证的明文协议。
- [ ] 模拟账户完成端到端验收后，由用户明确决定是否开启交易。

## 下一阶段：LEAN 与 lean-cli 部署

### D. 固定 LEAN 并构建自定义镜像

- [x] 目标 LEAN commit 已记录，Mac/Windows 均为
  `d72852f25e81cf4505a9059fc037c7c49cd21825`。
- [x] 通过幂等 Windows 安装脚本给 Launcher 增加 QMT `ProjectReference`。
- [x] 确认 Launcher 输出及 deps 清单包含 `QuantConnect.Brokerages.Qmt`。
- [x] 固定 Composer 发现链：Launcher output 扫描、`InheritedExport` Factory。
- [ ] 在 Windows Docker 构建并标记 `linux/amd64` 自定义 Engine 镜像。
- [ ] 镜像 tag 同时记录 LEAN SHA、QMT SHA 和协议版本。

推荐 tag：

```text
lean-cli/engine:qmt-YYYYMMDD-<lean-short-sha>-<qmt-short-sha>
```

### E. lean-cli 集成

- [x] 通过不会被 CDN 模块刷新覆盖的 local overlay 注册 QMT Brokerage。
- [x] 配置 `live-mode-brokerage = QmtBrokerage`。
- [x] 配置 `data-queue-handler = QmtBrokerage`。
- [x] 增加 Gateway host、port、account ID、timeout 和 trading-enabled 配置。
- [x] 在隔离的 Windows `lean-qmt.json` 增加 `live-qmt` environment。
- [ ] 固定 lean-cli fork commit/version 并用 editable 或固定版本安装。
- [ ] 验证 `lean live deploy` 生成的 `config.json` 和镜像参数。

lean-cli 是 Python 项目，不需要“编译 lean-cli 的 C#”。账号和其他敏感配置不得
提交到 Git。NuGet 是稳定后的可选分发方式，不是 MVP 或自定义镜像的前置条件。

### F. 模拟账户与生产前验收

- [ ] 容器通过 `host.docker.internal:17890` 完成账号握手。
- [ ] 只读验证资金、持仓、未完成委托和实时行情。
- [ ] 模拟账户验证下单、撤单、部分成交和完全成交。
- [ ] LEAN 收到并正确去重真实 `OnOrderEvent`。
- [ ] 测试 Gateway、LEAN 容器和网络分别中断后的恢复。
- [ ] 至少完成一个交易日的只读 soak test。
- [ ] 验证日志不包含密码、令牌或敏感订单 payload。
- [ ] 用户批准后才允许进入实盘检查清单。

目标启动顺序：

```text
1. 用户手工登录大 QMT
2. 用户手工运行 QMT Gateway 策略
3. 验证 Gateway 账号、协议版本和 trading_enabled
4. lean live deploy --environment live-qmt --image <qmt-image> --no-update
5. 验证资金、持仓、未完成委托和 China 行情
6. 进入策略运行
```

## 当前命令

```bash
make test          # 同步后在 Windows 运行全部 fake/离线测试
make test-windows  # 同 make test 的 Windows 工作流入口
make sync-windows  # 只同步工作树，不测试、不操作 QMT
make install-windows # 安装/验证 Launcher、lean-cli 和 lean-qmt.json
make image         # 在 Windows 构建自定义 QMT LEAN 镜像
make test-deployment # fake Gateway 的 lean-cli→镜像→Brokerage smoke
```

仍计划增加：

```bash
make test-integration  # 真实 QMT 模拟账户集成测试（必须显式人工准备）
make deploy-sim        # 模拟账户部署
make health            # 只读 Gateway/LEAN 健康检查
make stop              # 停止 LEAN 部署，不操作 QMT 客户端
```

## 当前阻塞与最短下一步

MVP 的 fake/离线闭环、Launcher 接入和 lean-cli QMT module 已完成。Windows Launcher
已经以 .NET 10 构建成功且输出 QMT DLL。当前 Docker Desktop 首次启动停在许可条款页，
需用户本人在 RDP 中接受后，才能完成镜像及容器 smoke；自动化不能代为接受条款。

最短下一步：保持交易关闭，由用户在真实大 QMT 中手工运行 Gateway 入口；只做
`hello → query_account → query_positions → query_orders → subscribe`，保存日志并确认
字段。只读证据稳定后，再安排模拟账户的最小下单/撤单测试。

## 进度记录

| 日期 | 变更 | 验证证据 |
|---|---|---|
| 2026-08-13 | 建立模板式工程、Python 3.11.13 环境和 Windows 测试链路 | `.test-logs/windows-test.log` |
| 2026-08-13 | Mac/Windows LEAN 与 Brokerage 对齐 .NET 10 | Windows SDK `10.0.400`，build 0 errors |
| 2026-08-13 | 完成协议 v1、Python Gateway、C# 客户端和 Brokerage MVP | Python 14/14；NUnit 51/51 |
| 2026-08-13 | 固定交易双开关和 fake-only 自动测试安全边界 | trading-disabled Python/C# 测试通过 |
| 2026-08-13 | 完成 Windows Launcher/lean-cli 幂等接入和隔离配置 | QMT DLL 进入 Launcher output；CLI module load 通过 |
