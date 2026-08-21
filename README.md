# Lean.Brokerages.QMT

LEAN Brokerage integration for the Guojin Big QMT client. QMT is the brokerage
and execution adapter; the MVP market scope is China A-share equities on the
Shanghai, Shenzhen, and Beijing exchanges.

The repository follows QuantConnect's
[`Lean.Brokerages.Template`](https://github.com/QuantConnect/Lean.Brokerages.Template)
layout. The C# projects and both LEAN checkouts target .NET 10. Offline Python
development uses Python 3.11.13, while the code loaded by QMT remains compatible
with QMT's customized Python 3.6.8 runtime.

The Mac and Windows LEAN checkouts are fixed to commit
`d72852f25e81cf4505a9059fc037c7c49cd21825`.

## MVP status

Implemented and validated by contract tests and real-QMT read-only E2E:

- NDJSON-over-TCP protocol v1 with account-checked `hello`, request IDs,
  timeouts, errors, events, and duplicate-request caching;
- account cash, positions, and open-order queries;
- China A-share symbol conversion for `.SH`, `.SZ`, and `.BJ` codes;
- Market and Limit orders, cancel, and an explicit unsupported result for
  order updates;
- tick subscriptions and quote conversion to LEAN data;
- QMT order/deal callbacks converted to LEAN `OrderEvent` instances;
- `QmtBrokerageFactory`, `QmtBrokerageModel`, `QmtBrokerage`, and the shared
  `IDataQueueHandler` instance.

Not yet production-ready:

- no simulated-account end-to-end run against a real QMT process;
- no automatic reconnect, resubscription, startup reconciliation, or event
  deduplication after reconnect;
- no full trading-day soak, network interruption, or restart test;
- no final field validation from captured real QMT query/callback logs.

The protocol contract is recorded in
[`docs/adr/0001-qmt-gateway-protocol.md`](docs/adr/0001-qmt-gateway-protocol.md).
The request/order state split and QMT status mapping are recorded in
[`docs/adr/0002-order-request-and-status-lifecycle.md`](docs/adr/0002-order-request-and-status-lifecycle.md).
The remaining deployment work is tracked in [ROADMAP.md](ROADMAP.md).

QMT is the Brokerage name, not the LEAN market ID. Add China equities with:

```python
self.add_equity("600000", Resolution.MINUTE, market="china")
```

The plugin registers the `china` market ID, Shanghai time zone, weekday
09:30–11:30/13:00–15:00 sessions, CNY quote currency, and a 0.01 price step.
The China holiday calendar is still a production-readiness item.

The Gateway handshake identifies whether the connected QMT terminal is the
simulation runtime, so no environment or market-order-style configuration is
required. Strategy calls remain ordinary LEAN `MarketOrder` calls.

Simulation accounts automatically use `latest-price`, which maps to QMT price
type `5` and is not an exchange-native market order. Live accounts automatically
use `five-level-immediate-or-cancel`, mapped to `42` on Shanghai/Beijing and `47`
on Shenzhen. A simulation account rejects orders outside its weekday 10:00–17:00
session before calling QMT because `passorder` otherwise drops them without an
order or rejection callback. QMT documents native stock market price types `42`
through `48` as unavailable in simulation trading.

## Python 策略类型存根

将 QMT 类型包安装到编写 LEAN 策略时使用的 Python 环境中：

```bash
make install-python-stubs
```

该包为策略使用的 QMT API 提供静态类型，并在策略通过 LEAN 运行时使用
Python.NET 加载真正的 `QuantConnect.Brokerages.Qmt` 程序集。QMT DLL 仍须作为
LEAN Brokerage 模块安装。

```python
from QuantConnect.Brokerages.Qmt import (
    QmtBrokerageModel,
    QmtMarketOrderStyle,
    QmtOrderProperties,
)

self.set_brokerage_model(QmtBrokerageModel())

order_properties = QmtOrderProperties()
order_properties.market_order_style = QmtMarketOrderStyle.LATEST_PRICE
self.market_order(symbol, 100, order_properties=order_properties)
```

市场单样式是可选项。未指定时，Brokerage 会为模拟账户选择
`latest-price`，为实盘账户选择 `five-level-immediate-or-cancel`。

## Repository layout

```text
QuantConnect.QmtBrokerage/
  QmtBrokerage.cs             LEAN Brokerage and live-data adapter
  QmtBrokerageFactory.cs      LEAN factory/configuration wiring
  QmtBrokerageModel.cs        MVP order and security capabilities
  QmtGatewayClient.cs         NDJSON TCP client
  QmtProtocol.cs              Protocol v1 DTOs
  QmtSymbolMapper.cs          China A-share/QMT code conversion
QuantConnect.QmtBrokerage.Tests/
  E2E/ReadOnly/               Real-QMT non-trading E2E
  E2E/Trading/                Real-QMT simulation-account trading E2E
  E2E/Infrastructure/         Shared categories and real-Gateway test context
  ...                         Mapping, model, and protocol contract tests
qmt_python/
  qmt_gateway_entry.py        Stable code copied into a QMT strategy once
  lean_qmt_gateway.py         Reloadable Gateway implementation
  qmt_local_config.example.py Local configuration template
  qmt_readonly_probe_entry.py Earlier read-only diagnostic entry
python_stubs/
  QuantConnect/Brokerages/Qmt QMT strategy type stubs and runtime loader
tests/
  test_python_compatibility.py QMT Python 3.6 syntax validation
scripts/
  sync_worktree_to_windows.sh Git branch sync over SSH
  test_windows.ps1            Authoritative Windows test runner
```

The default-engine and local Brokerage deployment procedure is documented in
[`docs/windows-deployment.md`](docs/windows-deployment.md).

## Authoritative tests

Run from the repository root on the Mac:

```bash
make test
```

The command requires a clean Git worktree, pushes the current branch, and
fast-forwards the same branch on Windows. It then runs Python compatibility
tests, builds changed C# inputs, runs non-explicit NUnit contract tests, and
copies the QMT assembly into the local module directory selected from the
default LEAN image's `lean_version` and `target_framework` labels. A manifest
beside the packaged DLL maps the tracked C# inputs, LEAN commit, engine labels,
and .NET SDK to the DLL hash. An exact match reuses the verified DLL and runs
NUnit with `--no-build`. It does not connect to QMT and it never submits an
order. Real Gateway validation uses the explicit read-only E2E commands below.
Console output prints the active task path, for example
`test-smoke > test > csharp-build`; successful command details remain in the
full Windows log instead of being repeated on the console.

The authoritative Windows checkout is:

```text
C:\Users\nemo\lean\Lean.Brokerages.QMT
```

The Windows runner requires .NET 10 from
`C:\Users\nemo\.dotnet\dotnet.exe` and Python 3.11.13 in the repository
`.venv`. Mac is the source/transport host; Windows build and test results are
authoritative. Structured stage output and the complete remote output are
saved to:

```text
.test-logs/windows-test.log
```

Latest recorded Windows evidence:

```text
Python tests: passed
.NET build: 0 errors
NUnit tests: passed
```

Commit the local branch before synchronization. To push it to `origin` and
fast-forward the same branch on Windows without testing:

```bash
make sync-windows
```

The repository exposes focused Make targets. Use `test` after Brokerage changes,
`package-windows` to sync and ensure the matching verified DLL is published,
`test-readonly` for the real-QMT Brokerage E2E, and `test-smoke` for the complete
LEAN live path:

```bash
make sync-windows
make package-windows
make test
make test-readonly
make test-smoke
make test-trading
make test-trading-inventory
```

`make test-readonly` runs the real Brokerage NUnit test, which checks the
account handshake, cash, holdings, open orders, daily/minute history,
subscription lifecycle, and an explicit disconnect/connect cycle.
`make test-smoke` first ensures the matching verified module is packaged, then
runs the complete
`lean-cli -> Docker -> LEAN Engine -> QMT` path. Both require trading to be
disabled, and neither calls an order method. During closed market hours the live
tick stage is reported as skipped, not passed. Neither test claims automatic
fault recovery.
The latest concise Brokerage evidence is served from Windows at:

```text
http://192.168.50.135:8000/e2e/qmt-readonly-e2e.log
http://192.168.50.135:8000/e2e/test-smoke.log
```

Windows live logs are served read-only through Nginx on the LAN:

```text
http://192.168.50.135:8000/smoke_test/
http://192.168.50.135:8000/broker/
http://192.168.50.135:8000/a-top-gainer/
http://192.168.50.135:8000/e2e/
```

The physical log directories live under `C:\Users\nemo\lean_logs`. Project
`live` paths are symbolic links into that root, and the Gateway writes directly
to `lean_logs\broker\qmt-gateway-runtime.log`. The Python Gateway rotates that
file at 5 MiB and keeps three backups. Build and deployment test logs remain
private under the repository `.test-logs`. Windows serves its own logs directly;
they are not copied to macOS. Native Windows Nginx serves only the unified root
directory on port 8000 and runs as the `QmtLiveLogs` startup task. Log access is
independent of Docker Desktop, WSL, and LEAN containers.

## Download QMT minute history

Download one ticker and trading date through the running QMT Gateway:

```bash
uv run --locked python scripts/download_qmt_history.py \
  --ticker 600000.SH \
  --date 20260814
```

The script writes the LEAN/QC minute ZIP to
`data/equity/china/minute/600000/20260814_trade.zip`. On Windows the default
data root is `C:\Users\nemo\lean_project\data`; on macOS it is
`~/Workspace/quant/lean-project/data`. On macOS the Gateway defaults to
`192.168.50.135`, so only `--ticker` and `--date` are required. Use
`--data-root` or `--host` only when overriding these defaults.

## One-time QMT Gateway setup

QMT must be logged in and the Gateway strategy must be started manually. The
repository scripts do not start, stop, restart, or operate the QMT client.

1. Synchronize the repository to Windows with `make sync-windows`.
2. On Windows, create the ignored local configuration:

   ```powershell
   Set-Location C:\Users\nemo\lean\Lean.Brokerages.QMT
   Copy-Item qmt_python\qmt_local_config.example.py qmt_python\qmt_local_config.py
   ```

3. Set `ACCOUNT_ID` in `qmt_python\qmt_local_config.py`.
4. In the Big QMT strategy editor, create/open a strategy and copy the complete
   contents of `qmt_python\qmt_gateway_entry.py` into it once. The QMT model
   import dialog accepts packaged strategy files, so do not try to import this
   source directory through that dialog.
5. Select the intended QMT account and manually run the strategy. A successful
   start includes logs similar to:

   ```text
   [lean_qmt_gateway] init_start ...
   [lean_qmt_gateway] server_started bind_host=127.0.0.1 bind_port=17890
   [lean_qmt_gateway] init_complete ...
   ```

The entry file watches
`C:\Users\nemo\lean\Lean.Brokerages.QMT\qmt_python\lean_qmt_gateway.py`
and automatically reloads it after Git synchronization. It compiles the new
source before stopping the current Gateway, does not register a second timer,
and rolls back to the previous module if initialization fails. Updating an
older installed entry to this hot-reload version requires one final manual
strategy restart; later Gateway changes do not. This direct loader avoids
`importlib`, which is absent from QMT's trimmed Python 3.6 runtime.

The default `127.0.0.1` binding is suitable for a LEAN process running directly
on Windows. A Docker deployment will require a protected non-loopback binding,
`GATEWAY_ALLOW_REMOTE_CLIENTS = True`, and a restrictive Windows firewall rule.
The protocol is plaintext and unauthenticated; port `17890` must never be
exposed to the public Internet.

## Trading validation

The Gateway and Brokerage do not have trading-enable configuration switches.
When connected to QMT, calls to `PlaceOrder()` and `CancelOrder()` are sent to
the configured account. The read-only tests never call either operation.

The explicit order/cancel test against the current QMT Gateway account is:

```bash
make test-trading
```

The repeatable category is fixed to `600000.SH` and `100` shares. It requires
the Gateway handshake account to match `lean-qmt.json`, and selects cases for
the QMT simulation session (`10:00-17:00` Asia/Shanghai). During the session it
places a non-marketable limit order, validates `Submitted`, cancellation and
the final `query_orders` state. Outside the session it requires an explicit
`latest-price` market order to raise `MarketClosed`. Local invalid-order cases
run with the normal unit/contract suite instead of connecting to QMT. Cleanup
does not pass until a remaining order reaches `Canceled` and disappears from
the open-order query.

The stateful market-buy case has a separate, explicit command:

```bash
make test-trading-inventory
```

It runs only during the simulation session, buys 100 shares, and verifies the
fill plus the exact 100-share holding increase. Each invocation intentionally
adds 100 T+0 shares. Concise Windows logs are:

```text
http://192.168.50.135:8000/e2e/test-trading.log
http://192.168.50.135:8000/e2e/test-trading-inventory.log
```
