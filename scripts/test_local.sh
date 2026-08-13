#!/usr/bin/env bash

set -euo pipefail

repository_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
uv_executable="${UV:-uv}"
test_started_at_seconds="$(date +%s)"

cd "$repository_directory"

echo "[qmt-test] host=mac stage=environment status=start command=\"$uv_executable sync --locked\""
"$uv_executable" sync --locked
python_version="$("$uv_executable" run --locked python --version 2>&1)"
echo "[qmt-test] host=mac stage=environment status=ok python=\"$python_version\""

python_tests_started_at_seconds="$(date +%s)"
echo "[qmt-test] host=mac stage=python-tests status=start command=\"$uv_executable run --locked python -m unittest discover -s tests -v\""
"$uv_executable" run --locked python -m unittest discover -s tests -v
python_tests_duration_seconds="$(( $(date +%s) - python_tests_started_at_seconds ))"
echo "[qmt-test] host=mac stage=python-tests status=ok duration_seconds=$python_tests_duration_seconds"

test_duration_seconds="$(( $(date +%s) - test_started_at_seconds ))"
echo "[qmt-test] host=mac stage=local status=ok duration_seconds=$test_duration_seconds"
