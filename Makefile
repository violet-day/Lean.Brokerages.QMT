.PHONY: test test-windows sync-windows

test:
	@echo '[qmt-test] host=mac stage=all status=start workflow="sync -> Windows Python tests -> Windows build -> Windows NUnit tests"'
	@./scripts/sync_worktree_to_windows.sh --test
	@echo '[qmt-test] host=mac stage=all status=ok'

test-windows:
	@./scripts/sync_worktree_to_windows.sh --test

sync-windows:
	@./scripts/sync_worktree_to_windows.sh
