# QMT Brokerage 开发与部署路线图

本文档是 `Lean.Brokerages.QMT` 从开发到 `lean-cli` 实盘部署的唯一进度清单。
完成任务后应同时更新复选框、验证证据和“当前阻塞”。

最后更新：2026-08-13

## 目标架构

```text
Windows x64 宿主机
├── 国金大 QMT
│   └── QMT Python Gateway（QMT 内嵌 Python 3.6.8）
│       ├── 查询、下单、撤单
│       ├── 行情订阅
│       └── 委托、成交、持仓回调
│
└── Docker Desktop（Linux/amd64）
    └── 自定义 LEAN Engine 镜像
        ├── LEAN 策略
        └── QuantConnect.Brokerages.Qmt.dll
            └── 通过 host.docker.internal 连接 QMT Gateway
```

订单事件闭环：

```text
LEAN PlaceOrder
→ QmtBrokerage
→ QMT Gateway
→ 大 QMT
→ order/deal callback
→ QMT Gateway
→ QmtBrokerage OnOrderEvent
→ LEAN 策略
```

## 已确认的工程决策

- [x] 项目结构参考 QuantConnect 官方 `Lean.Brokerages.Template`。
- [x] Mac 和 Windows 项目虚拟环境都固定为 Python `3.11.13`。
- [x] QMT 策略代码单独兼容 QMT 自带的定制 Python `3.6.8`。
- [x] `make test` 以 Windows 的编译和测试结果为权威结果。
- [x] QMT 客户端由用户手工登录、启动和停止；自动化不得拉起或重启 QMT。
- [x] 开发阶段使用 `ProjectReference` 把 Brokerage 编入 LEAN。
- [ ] 稳定后再决定是否发布 NuGet；NuGet 不是首版部署的前置条件。
- [ ] 固定最终使用的 LEAN commit 和目标框架。

当前重要版本差异：

```text
Windows 现有 LEAN checkout：net6.0
Mac 当前 LEAN checkout：net10.0
```

部署前必须消除这个差异。QMT Brokerage、LEAN Engine 和最终 Docker 镜像必须基于
同一个 LEAN commit、相同的 API 和相同的目标框架构建。Windows 的 net6 测试 DLL
不能直接放进当前 net10 镜像。

## 当前完成度

### 1. 工程与测试基础

- [x] 建立 Brokerage 主工程和 NUnit 测试工程。
- [x] 建立 `QuantConnect.QmtBrokerage.sln`。
- [x] 实现 QMT 股票代码解析测试。
- [x] 实现 QMT 委托状态到 LEAN `OrderStatus` 的映射测试。
- [x] 实现 Big QMT 查询类型契约测试。
- [x] 使用 uv 固定 Mac/Windows Python `3.11.13`。
- [x] 增加 QMT Python 3.6 语法兼容测试。
- [x] `make test` 同步当前工作树到 Windows。
- [x] Windows 执行 Python 测试、`dotnet build` 和 `dotnet test --no-build`。
- [x] Windows 测试日志保存到 `.test-logs/windows-test.log`。

### 2. QMT 内嵌 Python 探针

- [x] QMT 中只需复制一次 `qmt_readonly_probe_entry.py`。
- [x] 入口从 Git 工作区加载 `lean_qmt_readonly_probe.py`。
- [x] 避开 QMT 裁剪标准库中缺失的 `importlib`。
- [x] 已在 QMT 中运行入口，用户确认显示运行成功。
- [x] 只读查询账户、持仓、委托、成交的代码已存在。
- [x] 合约信息、历史行情和 tick 订阅探针已存在。
- [x] 账户、委托、成交、持仓 callback 探针已存在。
- [ ] 保存一份真实 QMT 运行日志作为接口字段证据。
- [ ] 根据真实日志固定各查询和 callback 的字段映射。

注意：当前探针不是 Gateway。它没有监听网络端口，LEAN 容器还无法连接它。

## 必须完成的实施阶段

### 阶段 A：定义 Gateway 协议

- [ ] 选择传输方式并写入 ADR；首选 TCP 上的请求/响应通道加事件推送通道。
- [ ] 定义协议版本和握手消息。
- [ ] 定义健康检查及账户身份校验。
- [ ] 定义请求 ID、超时、错误码和幂等规则。
- [ ] 定义账户、持仓、委托、成交查询消息。
- [ ] 定义下单、撤单消息。
- [ ] 定义行情订阅和退订消息。
- [ ] 定义委托、成交、持仓、连接状态事件。
- [ ] 为所有消息增加序列化契约测试。

验收标准：使用假 QMT 服务时，C# 客户端可以完成握手、查询、订阅、断线和重连测试。

### 阶段 B：实现 QMT Python Gateway

- [ ] 在 QMT 策略进程中监听本机端口。
- [ ] Gateway 默认仅监听受控接口，不向公网暴露。
- [ ] 实现健康检查、协议版本和账号校验。
- [ ] 实现账户、持仓、委托、成交查询。
- [ ] 实现行情订阅、退订和 tick 推送。
- [ ] 实现委托、成交、持仓 callback 推送。
- [ ] 在模拟账户实现下单和撤单。
- [ ] 增加请求日志、事件日志和异常日志。
- [ ] 增加断线恢复和重复请求防护。
- [ ] 验证策略停止后端口会关闭，重启后可恢复连接。

验收标准：独立测试客户端可以在模拟账户完成“查询→订阅→下单→成交回报”闭环。

### 阶段 C：实现完整 C# Brokerage

- [ ] `QmtGatewayClient`：连接、请求、事件流、超时和重连。
- [ ] `QmtBrokerageModel`：支持的证券、订单类型、时效和手续费规则。
- [ ] `QmtBrokerageFactory`：读取配置并创建单一 Brokerage/DataQueue 实例。
- [ ] `QmtBrokerage`：连接状态和生命周期。
- [ ] `GetCashBalance()`。
- [ ] `GetAccountHoldings()`。
- [ ] `GetOpenOrders()`。
- [ ] `PlaceOrder()`。
- [ ] `CancelOrder()`。
- [ ] 明确首版是否支持 `UpdateOrder()`；不支持时返回可解释错误。
- [ ] 将 QMT 委托/成交 callback 转成 LEAN `OrderEvent`。
- [ ] 实现 `IDataQueueHandler` 的订阅和退订。
- [ ] 将 QMT tick 转成 LEAN `Tick`/`TradeBar`。
- [ ] 实现符号映射和市场/交易所处理。
- [ ] 实现启动对账：资金、持仓、未完成委托。
- [ ] 实现断线重连后的状态恢复和事件去重。

验收标准：所有方法有单元测试；模拟账户集成测试能让 LEAN 收到真实 `OnOrderEvent`。

### 阶段 D：固定 LEAN 版本并集成构建

- [ ] 选择并记录目标 LEAN commit。
- [ ] 将 QMT 工程的目标框架对齐该 LEAN commit。
- [ ] 在 LEAN feature 分支为 Launcher 增加 QMT `ProjectReference`。
- [ ] 确认 `QuantConnect.Brokerages.Qmt.dll` 出现在 Launcher 输出目录。
- [ ] 确认 LEAN `Composer` 能发现 `QmtBrokerageFactory`。
- [ ] 将 QMT feature 分支加入 `build_lean.sh` 的 `MERGE_BRANCHES`。
- [ ] 在 Windows x64 的 Docker 环境构建 `linux/amd64` 引擎镜像。
- [ ] 为镜像记录 LEAN commit、QMT commit、协议版本和 tag。

推荐镜像 tag：

```text
lean-cli/engine:qmt-YYYYMMDD-<lean-short-sha>-<qmt-short-sha>
```

验收标准：在 Windows 执行镜像后，启动日志明确显示已加载
`QuantConnect.Brokerages.Qmt.dll` 和 `QmtBrokerageFactory`。

### 阶段 E：集成 lean-cli

- [ ] 在本地 `lean-cli/lean/modules-*.json` 注册 QMT。
- [ ] 注册 `live-mode-brokerage = QmtBrokerage`。
- [ ] 注册 `data-queue-handler = QmtBrokerage`。
- [ ] 增加 Gateway host、port、账号标识、超时等 CLI 配置项。
- [ ] 在 `lean.json` 增加 `live-qmt` environment。
- [ ] 使用 editable 安装或固定版本安装本地 lean-cli fork。
- [ ] 确认 `lean live deploy --help` 出现 QMT。
- [ ] 确认 CLI 生成的最终 `config.json` 包含 QMT 配置。

lean-cli 是 Python 项目。开发阶段修改模块清单后使用 editable 安装即可，不存在
“编译 lean-cli 的 C#”这一步；但应固定 lean-cli fork 的 commit/version。

建议环境：

```json
{
  "qmt-gateway-host": "host.docker.internal",
  "qmt-gateway-port": 17890,
  "environments": {
    "live-qmt": {
      "live-mode": true,
      "live-mode-brokerage": "QmtBrokerage",
      "data-queue-handler": ["QmtBrokerage"],
      "setup-handler": "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
      "result-handler": "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
      "data-feed-handler": "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
      "real-time-handler": "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
      "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
      "history-provider": [
        "BrokerageHistoryProvider",
        "SubscriptionDataReaderHistoryProvider"
      ]
    }
  }
}
```

账号和其他敏感配置不得提交到 Git。

### 阶段 F：部署与验收

- [ ] 增加 `make image`：通过 SSH 在 Windows Docker 构建引擎镜像。
- [ ] 增加 `make deploy-sim`：部署模拟账户。
- [ ] 增加 `make stop` 和只读健康检查命令。
- [ ] 验证容器可访问 `host.docker.internal:<gateway-port>`。
- [ ] 只读运行：查询和行情至少持续一个完整交易日。
- [ ] 模拟账户小额下单、撤单、部分成交和完全成交测试。
- [ ] 测试 QMT Gateway 重启、容器重启和网络中断。
- [ ] 测试 LEAN/QMT 持仓和未完成委托对账。
- [ ] 验证所有日志不包含密码等敏感信息。
- [ ] 完成实盘启用前检查表。
- [ ] 用户明确批准后才启用实盘下单。

最终启动顺序：

```text
1. 用户手工登录大 QMT
2. 用户手工运行 QMT Gateway 策略
3. 检查 Gateway 健康状态和账号
4. lean live deploy --environment live-qmt --image <qmt-image> --no-update
5. 验证资金、持仓、未完成委托和行情
6. 进入策略运行
```

## NuGet 发布（稳定后，可选）

- [ ] 确定包名和版本策略。
- [ ] 配置私有或公开 NuGet 源。
- [ ] 打包 DLL、依赖和必要资源。
- [ ] 在干净镜像中验证包安装。
- [ ] 如需官方模块式安装，再将 lean-cli 模块配置切换为 NuGet 安装。

NuGet 的价值是版本化分发，不会替代 Gateway、Brokerage 实现、LEAN 版本对齐或
集成测试。首版跑通前不应把它放在关键路径上。

## 日常命令规划

当前已有：

```bash
make test          # 同步到 Windows，编译并运行全部测试
make sync-windows  # 仅同步工作树
```

计划增加：

```bash
make test-integration  # 模拟 QMT Gateway 集成测试
make image             # Windows Docker 构建 linux/amd64 引擎
make deploy-sim        # 模拟账户部署
make health            # 查询 Gateway 和 LEAN 状态
make stop              # 停止 LEAN 部署，不操作 QMT 客户端
```

## 当前阻塞与下一步

当前不能执行 `lean live deploy --environment live-qmt`，因为：

1. QMT Python 目前是只读探针，不是网络 Gateway；
2. C# 项目还没有完整的 Brokerage、Factory 和 DataQueue 实现；
3. Windows net6 LEAN 与当前 net10 LEAN 尚未统一；
4. QMT DLL 尚未编入自定义 LEAN 镜像；
5. lean-cli 尚未注册 QMT。

下一步只做一件事：先定义 Gateway 协议，并用假服务完成 C# 客户端契约测试。
协议稳定后再分别实现 QMT Python 服务端和 C# Brokerage，避免两端同时猜接口。

## 进度记录

| 日期 | 变更 | 验证证据 |
|---|---|---|
| 2026-08-13 | 建立官方模板式 C# 工程和 Windows 测试链路 | Windows `dotnet build` 通过；NUnit 23/23 |
| 2026-08-13 | 固定 Mac/Windows Python 3.11.13，并兼容 QMT Python 3.6.8 | Python 测试和 QMT 运行入口通过 |
| 2026-08-13 | 明确自定义镜像、lean-cli 与 NuGet 的职责 | 本路线图记录实施顺序与验收标准 |
