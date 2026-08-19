QMT_ROOT_TASK := $(firstword $(MAKECMDGOALS))
ifeq ($(QMT_ROOT_TASK),)
QMT_ROOT_TASK := test
endif
export QMT_ROOT_TASK

task_path = $(if $(filter $(1),$(QMT_ROOT_TASK)),$(QMT_ROOT_TASK),$(QMT_ROOT_TASK) > $(1))

.PHONY: sync-windows package-windows test test-readonly test-smoke test-trading test-trading-inventory

test:
	@echo '[qmt-task] $(call task_path,test)'
	@echo '[qmt-test] host=mac stage=all status=start workflow="sync -> Windows Python tests -> Windows build-if-changed -> Windows NUnit tests -> package DLL"'
	@QMT_TASK_PATH='$(call task_path,test)' ./scripts/sync_worktree_to_windows.sh --test
	@echo '[qmt-test] host=mac stage=all status=ok'

sync-windows:
	@QMT_TASK_PATH='$(call task_path,sync-windows)' ./scripts/sync_worktree_to_windows.sh

package-windows:
	@echo '[qmt-task] $(call task_path,package-windows)'
	@QMT_TASK_PATH='$(call task_path,package-windows)' ./scripts/sync_worktree_to_windows.sh --package

test-readonly: package-windows
	@QMT_TASK_PATH='$(call task_path,test-readonly)' ./scripts/run_windows_deployment.sh test-readonly --skip-sync

test-smoke: package-windows
	@QMT_TASK_PATH='$(call task_path,test-smoke)' ./scripts/run_windows_deployment.sh test-smoke --skip-sync

test-trading: package-windows
	@QMT_TASK_PATH='$(call task_path,test-trading)' ./scripts/run_windows_deployment.sh test-trading --skip-sync

test-trading-inventory: package-windows
	@QMT_TASK_PATH='$(call task_path,test-trading-inventory)' ./scripts/run_windows_deployment.sh test-trading-inventory --skip-sync
