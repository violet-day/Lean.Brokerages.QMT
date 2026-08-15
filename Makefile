.PHONY: sync-windows install-windows test test-smoke test-trading

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
	@./scripts/run_windows_deployment.sh test-trading
