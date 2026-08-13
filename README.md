# Lean.Brokerages.QMT

LEAN brokerage integration for the Guojin Big QMT client.

The first milestone is a read-only Big QMT Python probe. It verifies the APIs
that are only available inside a QMT strategy process before the C# ZMQ client
and brokerage are implemented.

## Repository layout

```text
qmt_python/
  qmt_readonly_probe_entry.py  Stable file imported into QMT once
  lean_qmt_readonly_probe.py   Reloadable implementation synced by Git
  qmt_local_config.example.py  Local settings template
scripts/
  sync_windows.ps1             Fast-forward-only Windows Git sync
tests/
  test_qmt_readonly_probe.py   Offline fake-QMT tests
```

## Windows sync

The Windows checkout is expected at:

```text
C:\Users\nemo\lean\Lean.Brokerages.QMT
```

After the initial clone, update it with:

```powershell
powershell -ExecutionPolicy Bypass -File `
  C:\Users\nemo\lean\Lean.Brokerages.QMT\scripts\sync_windows.ps1
```

The script refuses to pull over local changes and only accepts a fast-forward
update. It prints the checked-out commit and the QMT entry path when complete.

## One-time QMT setup

1. Copy `qmt_python\qmt_local_config.example.py` to
   `qmt_python\qmt_local_config.py` and set `ACCOUNT_ID` locally. The local file
   is ignored by Git.
2. In Big QMT, import `qmt_python\qmt_readonly_probe_entry.py` once.
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

