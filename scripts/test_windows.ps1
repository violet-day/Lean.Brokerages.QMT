param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",

    [ValidateSet("sync", "test")]
    [string]$Action = "test"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$windowsTestStartedAt = Get-Date

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

$syncStartedAt = Get-Date
Write-Output "[qmt-test] host=windows stage=sync status=start source=$SourcePath destination=$RepositoryPath files=$($sourceFiles.Count)"

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
$syncDurationMilliseconds = [int]((Get-Date) - $syncStartedAt).TotalMilliseconds
Write-Output "[qmt-test] host=windows stage=sync status=ok files=$($sourceFiles.Count) duration_ms=$syncDurationMilliseconds"

if ($Action -eq "sync") {
    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=all status=ok action=sync duration_ms=$totalDurationMilliseconds"
    exit 0
}

Push-Location $RepositoryPath
try {
    $uvExecutable = (Get-Command uv -ErrorAction Stop).Source
    $environmentStartedAt = Get-Date
    Write-Output "[qmt-test] host=windows stage=environment status=start command=`"$uvExecutable sync --locked`""
    & $uvExecutable sync --locked
    if ($LASTEXITCODE -ne 0) {
        Write-Output "[qmt-test] host=windows stage=environment status=failed exit_code=$LASTEXITCODE"
        throw "uv sync failed with exit code $LASTEXITCODE."
    }

    $pythonExecutable = Join-Path $RepositoryPath ".venv\Scripts\python.exe"
    $pythonVersion = & $pythonExecutable --version 2>&1
    $environmentDurationMilliseconds = [int]((Get-Date) - $environmentStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=environment status=ok python=`"$pythonVersion`" duration_ms=$environmentDurationMilliseconds"

    $pythonTestsStartedAt = Get-Date
    Write-Output "[qmt-test] host=windows stage=python-tests status=start command=`"$pythonExecutable -m unittest discover -s tests -v`""
    & $pythonExecutable -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) {
        Write-Output "[qmt-test] host=windows stage=python-tests status=failed exit_code=$LASTEXITCODE"
        throw "Python tests failed with exit code $LASTEXITCODE."
    }
    $pythonTestsDurationMilliseconds = [int]((Get-Date) - $pythonTestsStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=python-tests status=ok duration_ms=$pythonTestsDurationMilliseconds"

    $dotnetVersion = & dotnet --version
    $dotnetBuildStartedAt = Get-Date
    Write-Output "[qmt-test] host=windows stage=dotnet-build status=start dotnet=$dotnetVersion command=`"dotnet build QuantConnect.QmtBrokerage.sln --configuration Release`""
    & dotnet build .\QuantConnect.QmtBrokerage.sln --configuration Release --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Output "[qmt-test] host=windows stage=dotnet-build status=failed exit_code=$LASTEXITCODE"
        throw ".NET build failed with exit code $LASTEXITCODE."
    }
    $dotnetBuildDurationMilliseconds = [int]((Get-Date) - $dotnetBuildStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=dotnet-build status=ok duration_ms=$dotnetBuildDurationMilliseconds"

    $dotnetTestsStartedAt = Get-Date
    Write-Output "[qmt-test] host=windows stage=dotnet-tests status=start command=`"dotnet test QuantConnect.QmtBrokerage.sln --no-build`""
    & dotnet test .\QuantConnect.QmtBrokerage.sln --configuration Release --no-build --no-restore --nologo --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        Write-Output "[qmt-test] host=windows stage=dotnet-tests status=failed exit_code=$LASTEXITCODE"
        throw ".NET tests failed with exit code $LASTEXITCODE."
    }
    $dotnetTestsDurationMilliseconds = [int]((Get-Date) - $dotnetTestsStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=dotnet-tests status=ok duration_ms=$dotnetTestsDurationMilliseconds"

    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-Output "[qmt-test] host=windows stage=all status=ok duration_ms=$totalDurationMilliseconds"
}
finally {
    Pop-Location
}
