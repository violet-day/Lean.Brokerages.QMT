#!/usr/bin/env bash

set -euo pipefail

repository_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
action="${1:-}"
account_id="${QMT_ACCOUNT_ID:-86033767}"
trading_account_id="${QMT_TRADING_ACCOUNT_ID:-}"
trading_symbol="${QMT_TRADING_SYMBOL:-600000.SH}"
trading_quantity="${QMT_TRADING_QUANTITY:-100}"
trading_limit_price="${QMT_TRADING_LIMIT_PRICE:-}"

if [[ "$action" != "install" && "$action" != "test-smoke" && "$action" != "test-trading" ]]; then
    echo "usage: $0 {install|test-smoke|test-trading}" >&2
    exit 2
fi

if [[ "$action" == "test-trading" ]]; then
    if [[ ! "$trading_account_id" =~ ^[0-9]+$ ]]; then
        echo "QMT_TRADING_ACCOUNT_ID must be the expected numeric simulation account ID." >&2
        exit 2
    fi
    if [[ ! "$trading_symbol" =~ ^[0-9]{6}\.(SH|SZ|BJ)$ ]]; then
        echo "QMT_TRADING_SYMBOL must use the format 600000.SH, 000001.SZ, or 430001.BJ." >&2
        exit 2
    fi
    if [[ ! "$trading_quantity" =~ ^[1-9][0-9]*$ ]]; then
        echo "QMT_TRADING_QUANTITY must be a positive whole number." >&2
        exit 2
    fi
    if [[ ! "$trading_limit_price" =~ ^[0-9]+([.][0-9]+)?$ ]] || [[ "$trading_limit_price" == "0" ]]; then
        echo "QMT_TRADING_LIMIT_PRICE must be a positive decimal number." >&2
        exit 2
    fi
fi

echo "[qmt-deploy] host=mac stage=sync status=start action=$action"
"$repository_directory/scripts/sync_worktree_to_windows.sh"
echo "[qmt-deploy] host=mac stage=sync status=ok action=$action"

case "$action" in
    install)
        powershell_script_path='C:\Users\nemo\lean\Lean.Brokerages.QMT\scripts\install_windows_lean_integration.ps1'
        remote_command="& '$powershell_script_path' -AccountId '$account_id'"
        ;;
    test-smoke)
        smoke_project_path='C:\Users\nemo\lean_project\china_smoke_test'
        smoke_test_path='C:\Users\nemo\lean\Lean.Brokerages.QMT\scripts\test_windows_deployment.ps1'
        remote_command="\$ErrorActionPreference = 'Stop'; git -C '$smoke_project_path' pull --ff-only; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; & '$smoke_test_path'"
        ;;
    test-trading)
        trading_test_path='C:\Users\nemo\lean\Lean.Brokerages.QMT\scripts\test_windows_brokerage_e2e_trading.ps1'
        remote_command="& '$trading_test_path' -ExpectedSimulationAccountId '$trading_account_id' -StockCode '$trading_symbol' -Quantity '$trading_quantity' -LimitPrice '$trading_limit_price'"
        ;;
esac

connection_target="$(zsh -ic 'print -r -- ${aliases[qmt]##* }')"
if [[ -z "$connection_target" ]]; then
    echo "The qmt alias does not contain an SSH target." >&2
    exit 1
fi

encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"
log_directory="$repository_directory/.test-logs"
log_path="$log_directory/windows-deployment-$action.log"
mkdir -p "$log_directory"

echo "[qmt-deploy] host=mac stage=windows status=start action=$action"
zsh -ic 'qmt "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $1"' -- "$encoded_remote_command" \
    2>&1 \
    | LC_ALL=C tr -d '\r' \
    | tee "$log_path"
echo "[qmt-deploy] host=mac stage=windows status=ok action=$action log=$log_path"
