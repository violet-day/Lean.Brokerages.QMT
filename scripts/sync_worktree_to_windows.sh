#!/usr/bin/env bash

set -euo pipefail

repository_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
windows_git_repository_directory='C:\Users\nemo\lean\Lean.Brokerages.QMT'
windows_workspace_directory='C:\Users\nemo\lean\Lean.Brokerages.QMT-workspace'
windows_workspace_manifest_path='C:\Users\nemo\lean\Lean.Brokerages.QMT-workspace-files'
windows_action='sync'
parent_task_path="${QMT_TASK_PATH:-}"
test_task_path="${parent_task_path:-${QMT_ROOT_TASK:-test}}"

if [[ "${1:-}" == "--test" ]]; then
    windows_action='test'
elif [[ "${1:-}" == "--package" ]]; then
    windows_action='package'
elif [[ -n "${1:-}" ]]; then
    echo "usage: $0 [--test|--package]" >&2
    exit 2
fi

test_log_directory="$repository_directory/.test-logs"
windows_test_log_name='windows-test.log'
if [[ "$windows_action" == 'package' ]]; then
    windows_test_log_name='windows-package.log'
fi
windows_test_log_path="$test_log_directory/$windows_test_log_name"

if [[ -z "$parent_task_path" ]]; then
    current_task_path="${QMT_ROOT_TASK:-sync-windows}"
elif [[ "$parent_task_path" == "sync-windows" || "$parent_task_path" == *" > sync-windows" ]]; then
    current_task_path="$parent_task_path"
else
    current_task_path="$parent_task_path > sync-windows"
fi
echo "[qmt-task] $current_task_path"

repository_branch="$(git -C "$repository_directory" symbolic-ref --quiet --short HEAD)"
repository_commit="$(git -C "$repository_directory" rev-parse HEAD)"
snapshot_file_count="$(git -C "$repository_directory" ls-files --cached --others --exclude-standard | wc -l | tr -d ' ')"
snapshot_change_count="$(git -C "$repository_directory" status --porcelain | wc -l | tr -d ' ')"
sync_started_at_seconds="$(date +%s)"

echo "[qmt-test] host=mac stage=git-push status=start branch=$repository_branch commit=$repository_commit"
git -C "$repository_directory" push origin "HEAD:refs/heads/$repository_branch"
echo "[qmt-test] host=mac stage=git-push status=ok branch=$repository_branch commit=$repository_commit"

invoke_windows_powershell() {
    local remote_command="$1"
    local encoded_remote_command
    encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"
    zsh -ic 'qmt "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $1"' -- "$encoded_remote_command"
}

prepare_workspace_command="\$ErrorActionPreference = 'Stop'; git -C '$windows_git_repository_directory' fetch origin '$repository_branch'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; if (-not (Test-Path -LiteralPath '$windows_workspace_directory')) { git -C '$windows_git_repository_directory' worktree add --detach '$windows_workspace_directory' '$repository_commit'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE } }; if (Test-Path -LiteralPath '$windows_workspace_manifest_path') { \$previousSnapshotBytes = [System.IO.File]::ReadAllBytes('$windows_workspace_manifest_path'); \$previousSnapshotFiles = [System.Text.Encoding]::UTF8.GetString(\$previousSnapshotBytes).Split([char]0) } else { \$previousSnapshotFiles = @(git -C '$windows_workspace_directory' ls-files) }; foreach (\$relativePath in \$previousSnapshotFiles) { if (-not [string]::IsNullOrWhiteSpace(\$relativePath)) { Remove-Item -LiteralPath (Join-Path '$windows_workspace_directory' \$relativePath) -Force -ErrorAction SilentlyContinue } }; '[qmt-test] host=windows stage=workspace status=ready path=$windows_workspace_directory base_commit=$repository_commit'"

extract_snapshot_command="\$ErrorActionPreference = 'Stop'; \$archiveBase64 = [Console]::In.ReadToEnd(); \$archivePath = [System.IO.Path]::GetTempFileName(); try { [System.IO.File]::WriteAllBytes(\$archivePath, [Convert]::FromBase64String(\$archiveBase64)); \$tarExecutable = (Get-Command tar.exe -ErrorAction Stop).Source; & \$tarExecutable -xzf \$archivePath -C '$windows_workspace_directory'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE } } finally { Remove-Item -LiteralPath \$archivePath -Force -ErrorAction SilentlyContinue }"

write_snapshot_manifest_command="\$ErrorActionPreference = 'Stop'; \$manifestBase64 = [Console]::In.ReadToEnd(); [System.IO.File]::WriteAllBytes('$windows_workspace_manifest_path', [Convert]::FromBase64String(\$manifestBase64)); '[qmt-test] host=windows stage=workspace-snapshot status=ok files=$snapshot_file_count changes=$snapshot_change_count path=$windows_workspace_directory'"

run_windows_command="\$ErrorActionPreference = 'Stop'; if ('$windows_action' -eq 'test') { & '$windows_workspace_directory\\scripts\\test_windows.ps1' -RepositoryPath '$windows_workspace_directory' -TaskPath '$test_task_path'; exit \$LASTEXITCODE }; if ('$windows_action' -eq 'package') { & '$windows_workspace_directory\\scripts\\test_windows.ps1' -RepositoryPath '$windows_workspace_directory' -TaskPath '$test_task_path' -EnsurePackage; exit \$LASTEXITCODE }"

remote_action_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=windows status=start action=$windows_action"
mkdir -p "$test_log_directory"
invoke_windows_powershell "$prepare_workspace_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee "$windows_test_log_path"
git -C "$repository_directory" ls-files --cached --others --exclude-standard -z \
    | tar -C "$repository_directory" --null -T - -czf - \
    | base64 \
    | invoke_windows_powershell "$extract_snapshot_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee -a "$windows_test_log_path"
git -C "$repository_directory" ls-files --cached --others --exclude-standard -z \
    | base64 \
    | invoke_windows_powershell "$write_snapshot_manifest_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee -a "$windows_test_log_path"
if [[ "$windows_action" != 'sync' ]]; then
    invoke_windows_powershell "$run_windows_command" 2>&1 \
        | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
        | tee -a "$windows_test_log_path"
fi
remote_action_duration_seconds="$(( $(date +%s) - remote_action_started_at_seconds ))"
sync_duration_seconds="$(( $(date +%s) - sync_started_at_seconds ))"
echo "[qmt-test] host=mac stage=windows status=ok action=$windows_action duration_seconds=$remote_action_duration_seconds"
echo "[qmt-test] host=mac stage=windows-log path=$windows_test_log_path"
echo "[qmt-test] host=mac stage=sync status=ok duration_seconds=$sync_duration_seconds"
