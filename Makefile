.PHONY: test test-windows package-windows sync-windows sync-smoke install-windows test-live

test:
	@echo '[qmt-test] host=mac stage=all status=start workflow="sync -> Windows Python tests -> Windows build -> Windows NUnit tests"'
	@./scripts/sync_worktree_to_windows.sh --test
	@echo '[qmt-test] host=mac stage=all status=ok'

test-windows:
	@./scripts/sync_worktree_to_windows.sh --test

package-windows: test-windows

sync-windows:
	@./scripts/sync_worktree_to_windows.sh

sync-smoke:
	@zsh -ic 'qmt "git -C C:\Users\nemo\lean_project\china_smoke_test pull --ff-only"'

install-windows:
	@./scripts/run_windows_deployment.sh install

test-live: sync-smoke
	@./scripts/run_windows_deployment.sh test
