#!/usr/bin/env bash

set -euo pipefail

repository_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
windows_repository_directory='C:\Users\nemo\lean\Lean.Brokerages.QMT'
run_windows_tests=false

if [[ "${1:-}" == "--test" ]]; then
    run_windows_tests=true
elif [[ -n "${1:-}" ]]; then
    echo "usage: $0 [--test]" >&2
    exit 2
fi

test_log_directory="$repository_directory/.test-logs"
windows_test_log_path="$test_log_directory/windows-test.log"

windows_action='sync'
if [[ "$run_windows_tests" == true ]]; then
    windows_action='test'
fi

if [[ -n "$(git -C "$repository_directory" status --porcelain)" ]]; then
    echo "Git synchronization requires a clean worktree. Commit the following changes first:" >&2
    git -C "$repository_directory" status --short >&2
    exit 1
fi

repository_branch="$(git -C "$repository_directory" symbolic-ref --quiet --short HEAD)"
repository_commit="$(git -C "$repository_directory" rev-parse HEAD)"
sync_started_at_seconds="$(date +%s)"

echo "[qmt-test] host=mac stage=git-push status=start branch=$repository_branch commit=$repository_commit"
git -C "$repository_directory" push origin "HEAD:refs/heads/$repository_branch"
echo "[qmt-test] host=mac stage=git-push status=ok branch=$repository_branch commit=$repository_commit"

remote_command="\$ErrorActionPreference = 'Stop'; Set-Location -LiteralPath '$windows_repository_directory'; if (git status --porcelain --untracked-files=no) { throw 'The Windows QMT repository has uncommitted tracked changes.' }; git fetch origin '$repository_branch'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; git switch '$repository_branch'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; git merge --ff-only 'origin/$repository_branch'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; \$windowsCommit = git rev-parse HEAD; if (\$windowsCommit -ne '$repository_commit') { throw \"Expected QMT commit $repository_commit, found \$windowsCommit.\" }; if ('$windows_action' -eq 'test') { & '.\\scripts\\test_windows.ps1' -RepositoryPath '$windows_repository_directory'; exit \$LASTEXITCODE }"
encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"

remote_action_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=windows status=start action=$windows_action"
mkdir -p "$test_log_directory"
zsh -ic 'qmt "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $1"' -- "$encoded_remote_command" \
    2>&1 \
    | LC_ALL=C tr -d '\r' \
    | tee "$windows_test_log_path"
remote_action_duration_seconds="$(( $(date +%s) - remote_action_started_at_seconds ))"
sync_duration_seconds="$(( $(date +%s) - sync_started_at_seconds ))"
echo "[qmt-test] host=mac stage=windows status=ok action=$windows_action duration_seconds=$remote_action_duration_seconds"
echo "[qmt-test] host=mac stage=windows-log path=$windows_test_log_path"
echo "[qmt-test] host=mac stage=sync status=ok duration_seconds=$sync_duration_seconds"
