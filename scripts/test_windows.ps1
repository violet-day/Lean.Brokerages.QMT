param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$RepositoryPath = "C:\Users\nemo\lean-net10\Lean.Brokerages.QMT",

    [ValidateSet("sync", "test")]
    [string]$Action = "test"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$windowsTestStartedAt = Get-Date
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
[Console]::InputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

function Write-WindowsTestLog {
    param([string]$Message)

    [Console]::Error.WriteLine($Message)
}

function Invoke-WindowsTestCommand {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $processStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processStartInfo.FileName = $Executable
    $processStartInfo.Arguments = $Arguments -join " "
    $processStartInfo.WorkingDirectory = (Get-Location).Path
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true
    $processStartInfo.RedirectStandardOutput = $true
    $processStartInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $processStartInfo
    [void]$process.Start()
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $standardOutput = $standardOutputTask.Result
    $standardError = $standardErrorTask.Result
    if ($standardOutput) {
        [Console]::Error.Write($standardOutput)
    }
    if ($standardError) {
        [Console]::Error.Write($standardError)
    }

    $exitCode = $process.ExitCode
    $process.Dispose()
    return $exitCode
}

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
Write-WindowsTestLog "[qmt-test] host=windows stage=sync status=start source=$SourcePath destination=$RepositoryPath files=$($sourceFiles.Count)"

if (Test-Path -LiteralPath $repositoryManifestPath) {
    foreach ($previousRelativeFilePath in Get-Content -LiteralPath $repositoryManifestPath) {
        if (-not $previousRelativeFilePath -or $sourceFileLookup.ContainsKey($previousRelativeFilePath)) {
            continue
        }

        $obsoleteFilePath = Join-Path $RepositoryPath $previousRelativeFilePath
        if (Test-Path -LiteralPath $obsoleteFilePath -PathType Leaf) {
            Remove-Item -LiteralPath $obsoleteFilePath -Force
            Write-WindowsTestLog "[qmt-test] host=windows stage=sync status=remove path=$previousRelativeFilePath"
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
Write-WindowsTestLog "[qmt-test] host=windows stage=sync status=ok files=$($sourceFiles.Count) duration_ms=$syncDurationMilliseconds"

if ($Action -eq "sync") {
    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=all status=ok action=sync duration_ms=$totalDurationMilliseconds"
    exit 0
}

Push-Location $RepositoryPath
try {
    $uvExecutable = (Get-Command uv -ErrorAction Stop).Source
    $environmentStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=environment status=start command=`"$uvExecutable sync --locked`""
    $commandExitCode = Invoke-WindowsTestCommand $uvExecutable @("sync", "--locked")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=environment status=failed exit_code=$commandExitCode"
        throw "uv sync failed with exit code $commandExitCode."
    }

    $pythonExecutable = Join-Path $RepositoryPath ".venv\Scripts\python.exe"
    $pythonVersion = & $pythonExecutable --version 2>&1
    $environmentDurationMilliseconds = [int]((Get-Date) - $environmentStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=environment status=ok python=`"$pythonVersion`" duration_ms=$environmentDurationMilliseconds"

    $pythonTestsStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=start command=`"$pythonExecutable -m unittest discover -s tests -v`""
    $commandExitCode = Invoke-WindowsTestCommand $pythonExecutable @("-m", "unittest", "discover", "-s", "tests", "-v")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=failed exit_code=$commandExitCode"
        throw "Python tests failed with exit code $commandExitCode."
    }
    $pythonTestsDurationMilliseconds = [int]((Get-Date) - $pythonTestsStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=ok duration_ms=$pythonTestsDurationMilliseconds"

    $dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
        throw ".NET 10 SDK is missing: $dotnetExecutable"
    }
    $dotnetVersion = & $dotnetExecutable --version
    if (-not $dotnetVersion.StartsWith("10.")) {
        throw "Expected .NET 10 SDK, found $dotnetVersion."
    }
    $dotnetBuildStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=start dotnet=$dotnetVersion command=`"$dotnetExecutable build QuantConnect.QmtBrokerage.sln --configuration Release`""
    $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("build", ".\QuantConnect.QmtBrokerage.sln", "--configuration", "Release", "--nologo", "--verbosity", "minimal")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=failed exit_code=$commandExitCode"
        throw ".NET build failed with exit code $commandExitCode."
    }
    $dotnetBuildDurationMilliseconds = [int]((Get-Date) - $dotnetBuildStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=ok duration_ms=$dotnetBuildDurationMilliseconds"

    $dotnetTestsStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=start command=`"dotnet test QuantConnect.QmtBrokerage.sln --no-build`""
    $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("test", ".\QuantConnect.QmtBrokerage.sln", "--configuration", "Release", "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=normal")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=failed exit_code=$commandExitCode"
        throw ".NET tests failed with exit code $commandExitCode."
    }
    $dotnetTestsDurationMilliseconds = [int]((Get-Date) - $dotnetTestsStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=ok duration_ms=$dotnetTestsDurationMilliseconds"

    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=all status=ok duration_ms=$totalDurationMilliseconds"
}
finally {
    Pop-Location
}
