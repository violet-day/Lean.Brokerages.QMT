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

temporary_directory="$(mktemp -d)"
control_socket="/tmp/qmt-worktree-sync-$$"
connection_target=""

cleanup() {
    if [[ -n "$connection_target" ]]; then
        ssh -S "$control_socket" -O exit "$connection_target" >/dev/null 2>&1 || true
    fi
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

file_manifest_path="$temporary_directory/file-manifest.txt"
package_directory="$temporary_directory/package"
archive_path="$temporary_directory/worktree.tar.gz"

sync_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=package status=start source=$repository_directory"
mkdir -p "$package_directory"
git -C "$repository_directory" ls-files --cached --others --exclude-standard -z > "$temporary_directory/file-manifest-null.txt"

while IFS= read -r -d '' relative_file_path; do
    if [[ ! -f "$repository_directory/$relative_file_path" ]]; then
        continue
    fi

    printf '%s\n' "$relative_file_path" >> "$file_manifest_path"
    mkdir -p "$package_directory/$(dirname "$relative_file_path")"
    cp "$repository_directory/$relative_file_path" "$package_directory/$relative_file_path"
done < "$temporary_directory/file-manifest-null.txt"

cp "$file_manifest_path" "$package_directory/.codex-sync-manifest"
COPYFILE_DISABLE=1 tar -czf "$archive_path" -C "$package_directory" .
file_count="$(wc -l < "$file_manifest_path" | tr -d ' ')"
archive_size_bytes="$(wc -c < "$archive_path" | tr -d ' ')"
echo "[qmt-test] host=mac stage=package status=ok files=$file_count archive_bytes=$archive_size_bytes"

windows_action='sync'
if [[ "$run_windows_tests" == true ]]; then
    windows_action='test'
fi

connection_target="$(zsh -ic 'print -r -- ${aliases[qmt]##* }')"
if [[ -z "$connection_target" ]]; then
    echo "The qmt alias does not contain an SSH target." >&2
    exit 1
fi

ssh_connection_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=ssh-connect status=start target=$connection_target"
zsh -ic '
    connection_command=${aliases[qmt]:-}
    if [[ -z "$connection_command" ]]; then
        print -u2 "The qmt alias is not defined."
        exit 1
    fi
    connection_prefix=${connection_command% *}
    connection_target=${connection_command##* }
    eval "$connection_prefix -M -S ${(q)1} -o ControlPersist=60 -fN ${(q)connection_target}"
' -- "$control_socket"
ssh_connection_duration_seconds="$(( $(date +%s) - ssh_connection_started_at_seconds ))"
echo "[qmt-test] host=mac stage=ssh-connect status=ok duration_seconds=$ssh_connection_duration_seconds"

remote_archive_path='C:/Users/nemo/lean/.Lean.Brokerages.QMT.sync.tar.gz'
transfer_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=transfer status=start destination=$remote_archive_path"
scp -q -o ControlPath="$control_socket" \
    "$archive_path" "${connection_target}:$remote_archive_path"
transfer_duration_seconds="$(( $(date +%s) - transfer_started_at_seconds ))"
echo "[qmt-test] host=mac stage=transfer status=ok duration_seconds=$transfer_duration_seconds bytes=$archive_size_bytes"

remote_command="\$ErrorActionPreference = 'Stop'; \$ProgressPreference = 'SilentlyContinue'; \$stagingDirectory = 'C:\\Users\\nemo\\lean\\.Lean.Brokerages.QMT.sync'; \$archivePath = 'C:\\Users\\nemo\\lean\\.Lean.Brokerages.QMT.sync.tar.gz'; if (Test-Path -LiteralPath \$stagingDirectory) { & cmd.exe /d /c rd /s /q \"\$stagingDirectory\"; if (\$LASTEXITCODE -ne 0) { throw 'Could not clean the Windows staging directory.' } }; New-Item -ItemType Directory -Path \$stagingDirectory | Out-Null; & tar.exe -xzf \$archivePath -C \$stagingDirectory; if (\$LASTEXITCODE -ne 0) { throw 'Could not extract worktree archive.' }; & \"\$stagingDirectory\\scripts\\test_windows.ps1\" -SourcePath \$stagingDirectory -RepositoryPath '$windows_repository_directory' -Action '$windows_action'; exit \$LASTEXITCODE"
encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"

remote_action_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=windows status=start action=$windows_action"
ssh -S "$control_socket" "$connection_target" \
    "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded_remote_command" \
    | tr -d '\r'
remote_action_duration_seconds="$(( $(date +%s) - remote_action_started_at_seconds ))"
sync_duration_seconds="$(( $(date +%s) - sync_started_at_seconds ))"
echo "[qmt-test] host=mac stage=windows status=ok action=$windows_action duration_seconds=$remote_action_duration_seconds"
echo "[qmt-test] host=mac stage=sync status=ok duration_seconds=$sync_duration_seconds"
