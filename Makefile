.PHONY: sync-windows install-windows test test-smoke test-trading

TRADING_SYMBOL ?= 600000.SH
TRADING_QUANTITY ?= 100

test:
	@echo '[qmt-test] host=mac stage=all status=start workflow="sync -> Windows Python tests -> Windows build -> Windows NUnit tests -> package DLL"'
	@./scripts/sync_worktree_to_windows.sh --test
	@echo '[qmt-test] host=mac stage=all status=ok'

sync-windows:
	@./scripts/sync_worktree_to_windows.sh

install-windows:
	@./scripts/run_windows_deployment.sh install

test-smoke:
	@./scripts/run_windows_deployment.sh test-smoke

test-trading:
	@test -n "$(TRADING_ACCOUNT_ID)" || { echo 'TRADING_ACCOUNT_ID is required.' >&2; exit 2; }
	@test -n "$(TRADING_LIMIT_PRICE)" || { echo 'TRADING_LIMIT_PRICE is required.' >&2; exit 2; }
	@QMT_TRADING_ACCOUNT_ID="$(TRADING_ACCOUNT_ID)" \
	 QMT_TRADING_SYMBOL="$(TRADING_SYMBOL)" \
	 QMT_TRADING_QUANTITY="$(TRADING_QUANTITY)" \
	 QMT_TRADING_LIMIT_PRICE="$(TRADING_LIMIT_PRICE)" \
	 ./scripts/run_windows_deployment.sh test-trading
