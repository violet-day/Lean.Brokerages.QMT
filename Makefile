.PHONY: test test-local test-windows sync-windows

PYTHON ?= python3

test: test-local test-windows

test-local:
	$(PYTHON) -m unittest discover -s tests -v

test-windows:
	./scripts/sync_worktree_to_windows.sh --test

sync-windows:
	./scripts/sync_worktree_to_windows.sh
