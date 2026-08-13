param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",

    [ValidateSet("sync", "test")]
    [string]$Action = "test"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$sourceManifestPath = Join-Path $SourcePath ".codex-sync-manifest"
$repositoryManifestPath = Join-Path $RepositoryPath ".codex-sync-manifest"

if (-not (Test-Path -LiteralPath $sourceManifestPath)) {
    throw "Missing worktree manifest: $sourceManifestPath"
}

$sourceFiles = @(Get-Content -LiteralPath $sourceManifestPath | Where-Object { $_ })
$sourceFileLookup = @{}
foreach ($relativeFilePath in $sourceFiles) {
    $sourceFileLookup[$relativeFilePath] = $true
}

if (Test-Path -LiteralPath $repositoryManifestPath) {
    foreach ($previousRelativeFilePath in Get-Content -LiteralPath $repositoryManifestPath) {
        if (-not $previousRelativeFilePath -or $sourceFileLookup.ContainsKey($previousRelativeFilePath)) {
            continue
        }

        $obsoleteFilePath = Join-Path $RepositoryPath $previousRelativeFilePath
        if (Test-Path -LiteralPath $obsoleteFilePath -PathType Leaf) {
            Remove-Item -LiteralPath $obsoleteFilePath -Force
            Write-Output "SYNC_REMOVE path=$previousRelativeFilePath"
        }
    }
}

foreach ($relativeFilePath in $sourceFiles) {
    $sourceFilePath = Join-Path $SourcePath $relativeFilePath
    $destinationFilePath = Join-Path $RepositoryPath $relativeFilePath
    $destinationDirectory = Split-Path -Parent $destinationFilePath

    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceFilePath -Destination $destinationFilePath -Force
}

Copy-Item -LiteralPath $sourceManifestPath -Destination $repositoryManifestPath -Force
Write-Output "SYNC_OK files=$($sourceFiles.Count) destination=$RepositoryPath"

if ($Action -eq "sync") {
    exit 0
}

$pythonExecutable = "C:\Users\nemo\anaconda3\python.exe"
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    $pythonExecutable = (Get-Command python -ErrorAction Stop).Source
}

Push-Location $RepositoryPath
try {
    Write-Output "TEST_START suite=python executable=$pythonExecutable"
    & $pythonExecutable -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) {
        throw "Python tests failed with exit code $LASTEXITCODE."
    }
    Write-Output "TEST_OK suite=python"

    Write-Output "TEST_START suite=dotnet solution=QuantConnect.QmtBrokerage.sln"
    & dotnet test .\QuantConnect.QmtBrokerage.sln --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw ".NET tests failed with exit code $LASTEXITCODE."
    }
    Write-Output "TEST_OK suite=dotnet"
}
finally {
    Pop-Location
}
