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

Implemented and covered by fake-Gateway tests:

- NDJSON-over-TCP protocol v1 with account-checked `hello`, request IDs,
  timeouts, errors, events, and duplicate-request caching;
- account cash, positions, and open-order queries;
- China A-share symbol conversion for `.SH`, `.SZ`, and `.BJ` codes;
- Market and Limit orders, cancel, and an explicit unsupported result for
  order updates;
- tick subscriptions and quote conversion to LEAN data;
- QMT order/deal callbacks converted to LEAN `OrderEvent` instances;
- `QmtBrokerageFactory`, `QmtBrokerageModel`, `QmtBrokerage`, and the shared
  `IDataQueueHandler` instance;
- two independent trading switches, both disabled by default.

Not yet production-ready:

- no simulated-account end-to-end run against a real QMT process;
- no automatic reconnect, resubscription, startup reconciliation, or event
  deduplication after reconnect;
- no LEAN image or `lean-cli` module integration;
- no full trading-day soak, network interruption, or restart test;
- no final field validation from captured real QMT query/callback logs.

The protocol contract is recorded in
[`docs/adr/0001-qmt-gateway-protocol.md`](docs/adr/0001-qmt-gateway-protocol.md).
The remaining deployment work is tracked in [ROADMAP.md](ROADMAP.md).

QMT is the Brokerage name, not the LEAN market ID. Add China equities with:

```python
self.add_equity("600000", Resolution.MINUTE, market="china")
```

The plugin registers the `china` market ID, Shanghai time zone, weekday
09:30–11:30/13:00–15:00 sessions, CNY quote currency, and a 0.01 price step.
The China holiday calendar is still a production-readiness item.

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
  QmtFakeGatewayServer.cs     Loopback-only fake Gateway
  ...                         Brokerage, client, mapping, and contract tests
qmt_python/
  qmt_gateway_entry.py        Stable code copied into a QMT strategy once
  lean_qmt_gateway.py         Reloadable Gateway implementation
  qmt_local_config.example.py Local configuration template
  qmt_readonly_probe_entry.py Earlier read-only diagnostic entry
tests/
  test_qmt_gateway.py         Fake-QMT Python tests
  ...                         Compatibility and read-only probe tests
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
fast-forwards the same branch on Windows. It then runs Python tests,
`dotnet build`, NUnit tests, and copies the QMT assembly into the local module
directory selected from the default LEAN image's `lean_version` and
`target_framework` labels. It does not connect to a real QMT process and it
never submits an order. All Gateway/Brokerage network tests use a loopback fake
server and fake QMT functions.

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
Python: 14/14 passed
.NET build: 0 errors
NUnit: 51/51 passed
```

Commit the local branch before synchronization. To push it to `origin` and
fast-forward the same branch on Windows without testing:

```bash
make sync-windows
```

To package the local Brokerage and run the real read-only deployment smoke
test with the default `quantconnect/lean:latest` image:

```bash
make install-windows
make package-windows
make test-live
```

Windows live logs are served read-only through Nginx on the LAN:

```text
http://192.168.50.135:8000/smoke_test/
http://192.168.50.135:8000/broker/
http://192.168.50.135:8000/a-top-gainer/
```

The physical log directories live under `C:\Users\nemo\lean_logs`. The
project `live`/`.test-logs` paths are symbolic links into that root. Nginx
mounts only the root directory. `make serve-live-logs` performs the one-time
migration and starts or replaces the Nginx container.

## One-time QMT Gateway setup

QMT must be logged in and the Gateway strategy must be started manually. The
repository scripts do not start, stop, restart, or operate the QMT client.

1. Synchronize the repository to Windows with `make sync-windows`.
2. On Windows, create the ignored local configuration:

   ```powershell
   Set-Location C:\Users\nemo\lean\Lean.Brokerages.QMT
   Copy-Item qmt_python\qmt_local_config.example.py qmt_python\qmt_local_config.py
   ```

3. Set `ACCOUNT_ID` in `qmt_python\qmt_local_config.py`. Leave
   `TRADING_ENABLED = False` for read-only and automated validation.
4. In the Big QMT strategy editor, create/open a strategy and copy the complete
   contents of `qmt_python\qmt_gateway_entry.py` into it once. The QMT model
   import dialog accepts packaged strategy files, so do not try to import this
   source directory through that dialog.
5. Select the intended QMT account and manually run the strategy. A successful
   start includes logs similar to:

   ```text
   [lean_qmt_gateway] init_start ...
   [lean_qmt_gateway] server_started bind_host=127.0.0.1 bind_port=17890 trading_enabled=False
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

## Trading safety

Trading requires both of these settings to be explicitly enabled:

```text
QMT qmt_local_config.py: TRADING_ENABLED = True
LEAN config:             qmt-trading-enabled = true
```

If either is false, `PlaceOrder()` and `CancelOrder()` are blocked. Keep both
false until the fake-Gateway suite, real-QMT read-only validation, and the
simulated-account checklist in ROADMAP.md have passed. Enabling either setting
is an operator decision and is never part of `make test`.
