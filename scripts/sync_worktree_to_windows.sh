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
tar -czf "$archive_path" -C "$package_directory" .

windows_action='sync'
if [[ "$run_windows_tests" == true ]]; then
    windows_action='test'
fi

connection_target="$(zsh -ic 'print -r -- ${aliases[qmt]##* }')"
if [[ -z "$connection_target" ]]; then
    echo "The qmt alias does not contain an SSH target." >&2
    exit 1
fi

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

remote_archive_path='C:/Users/nemo/lean/.Lean.Brokerages.QMT.sync.tar.gz'
scp -q -o ControlPath="$control_socket" \
    "$archive_path" "${connection_target}:$remote_archive_path"

remote_command="\$ErrorActionPreference = 'Stop'; \$ProgressPreference = 'SilentlyContinue'; \$stagingDirectory = 'C:\\Users\\nemo\\lean\\.Lean.Brokerages.QMT.sync'; \$archivePath = 'C:\\Users\\nemo\\lean\\.Lean.Brokerages.QMT.sync.tar.gz'; if (Test-Path -LiteralPath \$stagingDirectory) { Remove-Item -LiteralPath \$stagingDirectory -Recurse -Force }; New-Item -ItemType Directory -Path \$stagingDirectory | Out-Null; & tar.exe -xzf \$archivePath -C \$stagingDirectory; if (\$LASTEXITCODE -ne 0) { throw 'Could not extract worktree archive.' }; & \"\$stagingDirectory\\scripts\\test_windows.ps1\" -SourcePath \$stagingDirectory -RepositoryPath '$windows_repository_directory' -Action '$windows_action'; exit \$LASTEXITCODE"
encoded_remote_command="$(printf '%s' "$remote_command" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"

ssh -S "$control_socket" "$connection_target" \
    "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded_remote_command"
