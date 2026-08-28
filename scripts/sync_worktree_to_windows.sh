#!/usr/bin/env bash

set -euo pipefail

repository_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
windows_git_repository_directory='C:\Users\nemo\lean\Lean.Brokerages.QMT'
windows_workspace_directory='C:\Users\nemo\lean\Lean.Brokerages.QMT-workspace'
windows_workspace_manifest_path='C:\Users\nemo\lean\Lean.Brokerages.QMT-workspace-files'
windows_gateway_source_path='C:\Users\nemo\lean\Lean.Brokerages.QMT\qmt_python\lean_qmt_gateway.py'
windows_workspace_gateway_source_path='C:\Users\nemo\lean\Lean.Brokerages.QMT-workspace\qmt_python\lean_qmt_gateway.py'
windows_action='sync'
push_repository=true
parent_task_path="${QMT_TASK_PATH:-}"
test_task_path="${parent_task_path:-${QMT_ROOT_TASK:-test}}"

for argument in "$@"; do
    case "$argument" in
        --test)
            if [[ "$windows_action" != 'sync' ]]; then
                echo "usage: $0 [--test|--package] [--no-push]" >&2
                exit 2
            fi
            windows_action='test'
            ;;
        --package)
            if [[ "$windows_action" != 'sync' ]]; then
                echo "usage: $0 [--test|--package] [--no-push]" >&2
                exit 2
            fi
            windows_action='package'
            ;;
        --no-push)
            push_repository=false
            ;;
        *)
            echo "usage: $0 [--test|--package] [--no-push]" >&2
            exit 2
            ;;
    esac
done

test_log_directory="$repository_directory/.test-logs"
windows_test_log_name='windows-test.log'
if [[ "$windows_action" == 'package' ]]; then
    windows_test_log_name='windows-package.log'
fi
windows_test_log_path="$test_log_directory/$windows_test_log_name"
verified_package_input_fingerprint_path="$test_log_directory/verified-package-input.sha256"

list_snapshot_files() {
    git -C "$repository_directory" ls-files --cached --others --exclude-standard -z \
        | while IFS= read -r -d '' relative_path; do
            if [[ -e "$repository_directory/$relative_path" || -L "$repository_directory/$relative_path" ]]; then
                printf '%s\0' "$relative_path"
            fi
        done
}

calculate_package_input_fingerprint() {
    local relative_path

    {
        printf 'schema_version=1\n'
        while IFS= read -r -d '' relative_path; do
            case "$relative_path" in
                */bin/*|*/obj/*)
                    continue
                    ;;
            esac
            if [[ ! -f "$repository_directory/$relative_path" ]]; then
                continue
            fi
            printf 'file:%s\n' "$relative_path"
            shasum -a 256 "$repository_directory/$relative_path" | awk '{print $1}'
        done < <(
            git -C "$repository_directory" ls-files --cached --others --exclude-standard -z -- \
                QuantConnect.QmtBrokerage \
                QuantConnect.QmtBrokerage.Tests \
                qmt_python/lean_qmt_gateway.py \
                scripts \
                global.json
        )
    } | shasum -a 256 | awk '{print $1}'
}

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
snapshot_file_count="$(list_snapshot_files | tr -cd '\0' | wc -c | tr -d ' ')"
snapshot_change_count="$(git -C "$repository_directory" status --porcelain | wc -l | tr -d ' ')"
sync_started_at_seconds="$(date +%s)"
package_input_fingerprint=''

if [[ "$windows_action" != 'sync' ]]; then
    package_input_fingerprint="$(calculate_package_input_fingerprint)"
fi

if [[ "$windows_action" == 'package' ]]; then
    verified_package_input_fingerprint=''
    package_input_cache_miss_reason='verified-input-missing'
    if [[ -f "$verified_package_input_fingerprint_path" ]]; then
        verified_package_input_fingerprint="$(<"$verified_package_input_fingerprint_path")"
        package_input_cache_miss_reason='input-changed'
    fi
    if [[ "$package_input_fingerprint" == "$verified_package_input_fingerprint" ]]; then
        echo "[qmt-test] host=mac stage=package-input-cache status=hit action=skip-windows-package fingerprint=$package_input_fingerprint"
        exit 0
    fi
    echo "[qmt-test] host=mac stage=package-input-cache status=miss reason=$package_input_cache_miss_reason fingerprint=$package_input_fingerprint"
fi

if [[ "$push_repository" == true ]]; then
    echo "[qmt-test] host=mac stage=git-push status=start branch=$repository_branch commit=$repository_commit"
    git -C "$repository_directory" push origin "HEAD:refs/heads/$repository_branch"
    echo "[qmt-test] host=mac stage=git-push status=ok branch=$repository_branch commit=$repository_commit"
else
    echo "[qmt-test] host=mac stage=git-push status=skipped reason=local-snapshot branch=$repository_branch commit=$repository_commit"
fi

invoke_windows_powershell() {
    local remote_command="$1"
    local encoded_remote_command
    encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"
    zsh -ic 'qmt "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $1"' -- "$encoded_remote_command"
}

if [[ "$push_repository" == true ]]; then
    prepare_workspace_command="\$ErrorActionPreference = 'Stop'; git -C '$windows_git_repository_directory' fetch origin '$repository_branch'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }; if (-not (Test-Path -LiteralPath '$windows_workspace_directory')) { git -C '$windows_git_repository_directory' worktree add --detach '$windows_workspace_directory' '$repository_commit'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE } }; if (Test-Path -LiteralPath '$windows_workspace_manifest_path') { \$previousSnapshotBytes = [System.IO.File]::ReadAllBytes('$windows_workspace_manifest_path'); \$previousSnapshotFiles = [System.Text.Encoding]::UTF8.GetString(\$previousSnapshotBytes).Split([char]0) } else { \$previousSnapshotFiles = @(git -C '$windows_workspace_directory' ls-files) }; foreach (\$relativePath in \$previousSnapshotFiles) { if (-not [string]::IsNullOrWhiteSpace(\$relativePath)) { Remove-Item -LiteralPath (Join-Path '$windows_workspace_directory' \$relativePath) -Force -ErrorAction SilentlyContinue } }; '[qmt-test] host=windows stage=workspace status=ready path=$windows_workspace_directory base_commit=$repository_commit source=git'"
else
    prepare_workspace_command="\$ErrorActionPreference = 'Stop'; New-Item -ItemType Directory -Path '$windows_workspace_directory' -Force | Out-Null; if (Test-Path -LiteralPath '$windows_workspace_manifest_path') { \$previousSnapshotBytes = [System.IO.File]::ReadAllBytes('$windows_workspace_manifest_path'); \$previousSnapshotFiles = [System.Text.Encoding]::UTF8.GetString(\$previousSnapshotBytes).Split([char]0) } elseif (Test-Path -LiteralPath '$windows_workspace_directory\.git') { \$previousSnapshotFiles = @(git -C '$windows_workspace_directory' ls-files) } else { \$previousSnapshotFiles = @() }; foreach (\$relativePath in \$previousSnapshotFiles) { if (-not [string]::IsNullOrWhiteSpace(\$relativePath)) { Remove-Item -LiteralPath (Join-Path '$windows_workspace_directory' \$relativePath) -Force -ErrorAction SilentlyContinue } }; '[qmt-test] host=windows stage=workspace status=ready path=$windows_workspace_directory base_commit=$repository_commit source=local-snapshot'"
fi

extract_snapshot_command="\$ErrorActionPreference = 'Stop'; \$archiveBase64 = [Console]::In.ReadToEnd(); \$archivePath = [System.IO.Path]::GetTempFileName(); try { [System.IO.File]::WriteAllBytes(\$archivePath, [Convert]::FromBase64String(\$archiveBase64)); \$tarExecutable = (Get-Command tar.exe -ErrorAction Stop).Source; & \$tarExecutable -xzf \$archivePath -C '$windows_workspace_directory'; if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE } } finally { Remove-Item -LiteralPath \$archivePath -Force -ErrorAction SilentlyContinue }"

write_snapshot_manifest_command="\$ErrorActionPreference = 'Stop'; \$manifestBase64 = [Console]::In.ReadToEnd(); [System.IO.File]::WriteAllBytes('$windows_workspace_manifest_path', [Convert]::FromBase64String(\$manifestBase64)); '[qmt-test] host=windows stage=workspace-snapshot status=ok files=$snapshot_file_count changes=$snapshot_change_count path=$windows_workspace_directory'"

deploy_gateway_source_command="\$ErrorActionPreference = 'Stop'; \$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath '$windows_workspace_gateway_source_path').Hash; \$destinationHash = if (Test-Path -LiteralPath '$windows_gateway_source_path') { (Get-FileHash -Algorithm SHA256 -LiteralPath '$windows_gateway_source_path').Hash } else { '' }; if (\$sourceHash -ne \$destinationHash) { Copy-Item -LiteralPath '$windows_workspace_gateway_source_path' -Destination '$windows_gateway_source_path' -Force; \$action = 'update' } else { \$action = 'none' }; \$gatewaySource = Get-Item -LiteralPath '$windows_gateway_source_path'; \"[qmt-test] host=windows stage=gateway-source status=ok action=\$action bytes=\$(\$gatewaySource.Length) sha256=\$sourceHash path=\$(\$gatewaySource.FullName)\""

run_windows_command="\$ErrorActionPreference = 'Stop'; if ('$windows_action' -eq 'test') { & '$windows_workspace_directory\\scripts\\test_windows.ps1' -RepositoryPath '$windows_workspace_directory' -TaskPath '$test_task_path'; exit \$LASTEXITCODE }; if ('$windows_action' -eq 'package') { & '$windows_workspace_directory\\scripts\\test_windows.ps1' -RepositoryPath '$windows_workspace_directory' -TaskPath '$test_task_path' -EnsurePackage; exit \$LASTEXITCODE }"

remote_action_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=windows status=start action=$windows_action"
mkdir -p "$test_log_directory"
invoke_windows_powershell "$prepare_workspace_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee "$windows_test_log_path"
list_snapshot_files \
    | tar -C "$repository_directory" --null -T - -czf - \
    | base64 \
    | invoke_windows_powershell "$extract_snapshot_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee -a "$windows_test_log_path"
list_snapshot_files \
    | base64 \
    | invoke_windows_powershell "$write_snapshot_manifest_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee -a "$windows_test_log_path"
invoke_windows_powershell "$deploy_gateway_source_command" 2>&1 \
    | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
    | tee -a "$windows_test_log_path"
if [[ "$windows_action" != 'sync' ]]; then
    invoke_windows_powershell "$run_windows_command" 2>&1 \
        | LC_ALL=C perl -pe '$| = 1; s/\r//g' \
        | tee -a "$windows_test_log_path"
fi
if [[ -n "$package_input_fingerprint" ]]; then
    mkdir -p "$test_log_directory"
    printf '%s\n' "$package_input_fingerprint" > "$verified_package_input_fingerprint_path.tmp"
    mv "$verified_package_input_fingerprint_path.tmp" "$verified_package_input_fingerprint_path"
    echo "[qmt-test] host=mac stage=package-input-cache status=updated action=record-verified fingerprint=$package_input_fingerprint"
fi
remote_action_duration_seconds="$(( $(date +%s) - remote_action_started_at_seconds ))"
sync_duration_seconds="$(( $(date +%s) - sync_started_at_seconds ))"
echo "[qmt-test] host=mac stage=windows status=ok action=$windows_action duration_seconds=$remote_action_duration_seconds"
echo "[qmt-test] host=mac stage=windows-log path=$windows_test_log_path"
echo "[qmt-test] host=mac stage=sync status=ok duration_seconds=$sync_duration_seconds"
