# Lean.Brokerages.QMT

LEAN brokerage integration for the Guojin Big QMT client. The repository uses
the same solution/library/NUnit-test layout as QuantConnect's official
[`Lean.Brokerages.Template`](https://github.com/QuantConnect/Lean.Brokerages.Template).

The Windows LEAN checkout currently targets .NET 6, so this brokerage targets
`net6.0` even though the latest upstream template targets a newer .NET version.
The first milestone locks down the QMT/LEAN boundary with tests before adding
the concrete `Brokerage` and `IDataQueueHandler` implementations.

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

1. runs the Python tests locally;
2. copies the current worktree, including uncommitted files, to Windows;
3. preserves the ignored Windows `qmt_local_config.py`;
4. runs the Python tests and `dotnet test` on Windows.

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
reloads `lean_qmt_readonly_probe.py`, so the file does not need to be imported
again.

Expected log prefixes:

```text
[lean_qmt_probe] init_start
[lean_qmt_probe] query_ok type=ACCOUNT
[lean_qmt_probe] query_ok type=POSITION
[lean_qmt_probe] history_ok
[lean_qmt_probe] quote_subscription_ok
```
