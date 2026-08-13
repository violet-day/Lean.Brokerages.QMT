# Lean.Brokerages.QMT

LEAN brokerage integration for the Guojin Big QMT client. The repository uses
the same solution/library/NUnit-test layout as QuantConnect's official
[`Lean.Brokerages.Template`](https://github.com/QuantConnect/Lean.Brokerages.Template).

The Windows LEAN checkout currently targets .NET 6, so this brokerage targets
`net6.0` even though the latest upstream template targets a newer .NET version.
The first milestone locks down the QMT/LEAN boundary with tests before adding
the concrete `Brokerage` and `IDataQueueHandler` implementations.

Offline development and tests use the uv-managed Python 3.11.13 environment.
The QMT strategy itself runs inside QMT's trimmed embedded Python 3.6 runtime,
so the strategy bridge is also checked for Python 3.6 syntax compatibility.

Development status, deployment architecture and the tracked implementation
checklist are maintained in [ROADMAP.md](ROADMAP.md).

## Repository layout

```text
QuantConnect.QmtBrokerage.sln
QuantConnect.QmtBrokerage/
  QmtSecurityCode.cs           QMT stock-code parser
  QmtOrderStatusMapper.cs      QMT-to-LEAN order status mapping
  QmtTradeDetailType.cs        Big QMT read-only query constants
QuantConnect.QmtBrokerage.Tests/
  ...                          NUnit contract tests
qmt_python/
  qmt_readonly_probe_entry.py  Stable file imported into QMT once
  lean_qmt_readonly_probe.py   Reloadable implementation synced by Git
  qmt_local_config.example.py  Local settings template
scripts/
  sync_worktree_to_windows.sh  Sync current worktree over SSH
  test_windows.ps1             Run Windows Python and .NET tests
tests/
  test_qmt_readonly_probe.py   Offline fake-QMT tests
```

## Tests

From the repository root on the Mac:

```bash
make test
```

This command:

1. copies the current worktree, including uncommitted files, to Windows;
2. preserves the ignored Windows `qmt_local_config.py`;
3. creates/updates the Windows Python 3.11.13 `.venv` with uv;
4. runs all Python compatibility and probe tests on Windows;
5. builds the C# solution and runs all NUnit tests on Windows.

Mac is only the source and transport host for `make test`; no tests execute on
Mac. The uv-managed Mac Python 3.11.13 environment remains available for
development, but it is not part of the authoritative test workflow.

Every phase prints a `[qmt-test]` record with its host, stage, status and
duration. The Windows workflow runs `dotnet build` first, then executes
`dotnet test --no-build`, so compilation and test execution are visible as
separate stages. Remote output is normalized by the Mac process before it
reaches IDE consoles. The same compiler and NUnit output is always saved to
`.test-logs/windows-test.log`.

To sync without running the Windows tests:

```bash
make sync-windows
```

The Windows checkout is:

```text
C:\Users\nemo\lean\Lean.Brokerages.QMT
```

## One-time QMT setup

1. Copy `qmt_python\qmt_local_config.example.py` to
   `qmt_python\qmt_local_config.py` and set `ACCOUNT_ID` locally. The local file
   is ignored by Git.
2. Copy/import only `qmt_python\qmt_readonly_probe_entry.py` into Big QMT once.
3. Add the strategy to model trading and select the account.
4. Run it in live mode to enable account callbacks. This probe never calls an
   order or cancel function.

After future Git updates, rerun the existing QMT strategy. The stable entry
reloads `lean_qmt_readonly_probe.py` directly from source, so the file does not
need to be imported again. This avoids `importlib`, which is absent from QMT's
trimmed Python 3.6 standard library.

Expected log prefixes:

```text
[lean_qmt_probe] init_start
[lean_qmt_probe] query_ok type=ACCOUNT
[lean_qmt_probe] query_ok type=POSITION
[lean_qmt_probe] history_ok
[lean_qmt_probe] quote_subscription_ok
```
