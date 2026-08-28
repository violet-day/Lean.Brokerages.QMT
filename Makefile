QMT_ROOT_TASK := $(firstword $(MAKECMDGOALS))
ifeq ($(QMT_ROOT_TASK),)
QMT_ROOT_TASK := test
endif
export QMT_ROOT_TASK
export TEST_CASE

task_path = $(if $(filter $(1),$(QMT_ROOT_TASK)),$(QMT_ROOT_TASK),$(QMT_ROOT_TASK) > $(1))

LEAN_PYTHON_EXECUTABLE := /Users/Nemo/Workspace/quant/lean-project/.venv/bin/python
PYTHON_EXECUTABLE ?= python3
QMT_PUSH_REPOSITORY ?= true
qmt_push_option = $(if $(filter false 0 no,$(QMT_PUSH_REPOSITORY)),--no-push,)

.PHONY: calendar install-python-stubs sync-windows package-windows test test-readonly test-smoke test-trading

calendar:
	@$(PYTHON_EXECUTABLE) scripts/generate_china_trading_calendar.py --check

install-python-stubs:
	@echo '[qmt-task] $(call task_path,install-python-stubs)'
	@$(LEAN_PYTHON_EXECUTABLE) -m pip install --upgrade ./python_stubs

test: calendar
	@echo '[qmt-task] $(call task_path,test)'
	@echo '[qmt-test] host=mac stage=all status=start workflow="sync -> Windows Python tests -> Windows build-if-changed -> Windows NUnit tests -> package DLL"'
	@QMT_TASK_PATH='$(call task_path,test)' ./scripts/sync_worktree_to_windows.sh --test
	@echo '[qmt-test] host=mac stage=all status=ok'

sync-windows: calendar
	@QMT_TASK_PATH='$(call task_path,sync-windows)' ./scripts/sync_worktree_to_windows.sh

package-windows: calendar
	@echo '[qmt-task] $(call task_path,package-windows)'
	@QMT_TASK_PATH='$(call task_path,package-windows)' ./scripts/sync_worktree_to_windows.sh --package $(qmt_push_option)

test-readonly: package-windows
	@QMT_TASK_PATH='$(call task_path,test-readonly)' ./scripts/run_windows_deployment.sh test-readonly --skip-sync

test-smoke: package-windows
	@QMT_TASK_PATH='$(call task_path,test-smoke)' ./scripts/run_windows_deployment.sh test-smoke --skip-sync

test-trading: package-windows
	@QMT_TASK_PATH='$(call task_path,test-trading)' ./scripts/run_windows_deployment.sh test-trading --skip-sync
