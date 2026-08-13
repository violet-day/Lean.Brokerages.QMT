param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath (Join-Path $RepositoryPath ".git"))) {
    throw "Not a Git checkout: $RepositoryPath"
}

$pendingChanges = git -C $RepositoryPath status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status failed"
}
if ($pendingChanges) {
    throw "Refusing to sync over local changes:`n$pendingChanges"
}

git -C $RepositoryPath fetch origin $Branch
if ($LASTEXITCODE -ne 0) {
    throw "git fetch failed"
}

git -C $RepositoryPath checkout $Branch
if ($LASTEXITCODE -ne 0) {
    throw "git checkout failed"
}

git -C $RepositoryPath pull --ff-only origin $Branch
if ($LASTEXITCODE -ne 0) {
    throw "git pull --ff-only failed"
}

$commit = git -C $RepositoryPath rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) {
    throw "git rev-parse failed"
}

$entryPath = Join-Path $RepositoryPath "qmt_python\qmt_readonly_probe_entry.py"
Write-Output "SYNC_OK commit=$commit"
Write-Output "QMT_ENTRY=$entryPath"

