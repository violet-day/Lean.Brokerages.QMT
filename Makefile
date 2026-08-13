.PHONY: test test-local test-windows sync-windows

UV ?= uv

test:
	@echo '[qmt-test] host=mac stage=all status=start workflow="local tests -> sync -> Windows build -> Windows tests"'
	@$(MAKE) --no-print-directory test-local
	@$(MAKE) --no-print-directory test-windows
	@echo '[qmt-test] host=mac stage=all status=ok'

test-local:
	@UV=$(UV) ./scripts/test_local.sh

test-windows:
	@./scripts/sync_worktree_to_windows.sh --test

sync-windows:
	@./scripts/sync_worktree_to_windows.sh
