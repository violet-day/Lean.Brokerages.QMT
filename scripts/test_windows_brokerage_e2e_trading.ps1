param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanConfigurationPath = "C:\Users\nemo\lean_project\lean-qmt.json",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage",
    [int]$GatewayPort = 17890,
    [string]$TaskPath = "test-trading > trading-e2e",
    [string]$TestCategory = "QmtTradingRepeatable",
    [string]$LogFileName = "test-trading.log",
    [switch]$RequireCompleted,
    [string]$LeanVersion = "",
    [string]$TargetFramework = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
. (Join-Path $PSScriptRoot "windows_build_cache.ps1")

$privateLogDirectory = Join-Path $RepositoryPath ".test-logs"
$privateLogName = "windows-brokerage-e2e-$([System.IO.Path]::GetFileNameWithoutExtension($LogFileName))-full.log"
$privateLogPath = Join-Path $privateLogDirectory $privateLogName
$userLogDirectory = Join-Path $LogRootPath "e2e"
$userLogPath = Join-Path $userLogDirectory $LogFileName
New-Item -ItemType Directory -Path $privateLogDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $userLogDirectory -Force | Out-Null
[System.IO.File]::WriteAllText($privateLogPath, "", $utf8Encoding)
[System.IO.File]::WriteAllText($userLogPath, "", $utf8Encoding)

function Write-TradingEvidence {
    param([string]$Message)

    $line = "$(Get-Date -Format o) $Message"
    [System.IO.File]::AppendAllText($userLogPath, $line + "`r`n", $utf8Encoding)
    [Console]::Error.WriteLine($line)
}

function Write-CurrentTask {
    param([string]$CurrentTask)

    Write-TradingEvidence "[qmt-task] $TaskPath > $CurrentTask"
}

function Invoke-StreamingTestCommand {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $outputLines = New-Object System.Collections.Generic.List[string]
    $userFacingTestLinePattern = "\[qmt-task\]|\[qmt-trading-e2e\]|" +
        "^\s*(Passed|Failed|Skipped)\s|^Total tests:|" +
        "^\s+(Passed|Failed|Skipped):|^\s*Total time:|" +
        "^Test Run (Passed|Failed)\.|NUnit3TestExecutor discovered"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $Executable @Arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            [void]$outputLines.Add($line)
            [System.IO.File]::AppendAllText($privateLogPath, $line + "`r`n", $utf8Encoding)
            if ($line -match $userFacingTestLinePattern) {
                [Console]::Error.WriteLine($line)
            }
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $outputLines -join "`r`n"
    }
}

$currentStage = "preflight"
Write-TradingEvidence "[qmt-trading-e2e] stage=run status=start category=$TestCategory account_source=gateway_hello stock_code=600000.SH quantity=100"
try {
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    if (-not (Test-Path -LiteralPath $LeanConfigurationPath)) {
        throw "The QMT LEAN configuration is missing: $LeanConfigurationPath"
    }
    $configuration = Get-Content -LiteralPath $LeanConfigurationPath -Raw | ConvertFrom-Json
    $accountId = [string]$configuration."qmt-account-id"
    if (-not $accountId) {
        throw "qmt-account-id is missing from $LeanConfigurationPath"
    }
    $gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue
    if (-not $gatewayListener) {
        throw "The real QMT Gateway is not listening on Windows port $GatewayPort."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok gateway_port=$GatewayPort account_properties=gateway_hello"

    $currentStage = "build-cache"
    Write-CurrentTask "csharp-build"
    $dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
        throw ".NET SDK is missing: $dotnetExecutable"
    }
    $dotnetVersion = & $dotnetExecutable --version
    if ([bool]$LeanVersion -ne [bool]$TargetFramework) {
        throw "LeanVersion and TargetFramework must be supplied together."
    }
    if (-not $LeanVersion) {
        $dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
        $engineImageMetadata = (& $dockerExecutable image inspect $EngineImage | ConvertFrom-Json)[0]
        $TargetFramework = [string]$engineImageMetadata.Config.Labels.target_framework
        $LeanVersion = [string]$engineImageMetadata.Config.Labels.lean_version
        if (-not $TargetFramework -or -not $LeanVersion) {
            throw "The LEAN image does not declare lean_version and target_framework: $EngineImage"
        }
    }
    $buildCacheState = Get-QmtWindowsBuildCacheState `
        -RepositoryPath $RepositoryPath `
        -ModuleRoot $ModuleRoot `
        -LeanVersion $LeanVersion `
        -TargetFramework $TargetFramework `
        -DotnetVersion $dotnetVersion
    if (-not $buildCacheState.IsBuildCacheHit) {
        Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=miss reason=$($buildCacheState.BuildCacheMissReason) fingerprint=$($buildCacheState.BuildFingerprint)"
        & (Join-Path $PSScriptRoot "test_windows.ps1") `
            -RepositoryPath $RepositoryPath `
            -EngineImage $EngineImage `
            -ModuleRoot $ModuleRoot `
            -TaskPath "$TaskPath > ensure-build" `
            -LeanVersion $LeanVersion `
            -TargetFramework $TargetFramework `
            -EnsurePackage
        if ($LASTEXITCODE -ne 0) {
            throw "The shared QMT build and contract tests failed."
        }
        $buildCacheState = Get-QmtWindowsBuildCacheState `
            -RepositoryPath $RepositoryPath `
            -ModuleRoot $ModuleRoot `
            -LeanVersion $LeanVersion `
            -TargetFramework $TargetFramework `
            -DotnetVersion $dotnetVersion
        if (-not $buildCacheState.IsBuildCacheHit) {
            throw "The shared QMT build cache is still invalid: $($buildCacheState.BuildCacheMissReason)"
        }
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=hit action=skip-build fingerprint=$($buildCacheState.BuildFingerprint) dll_sha256=$($buildCacheState.PackagedAssemblyHash)"
    $testProjectPath = $buildCacheState.TestProjectPath

    $currentStage = "brokerage-trading-test"
    Write-CurrentTask "brokerage-trading-test"
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    $env:QMT_TRADING_E2E_GATEWAY_HOST = "127.0.0.1"
    $env:QMT_TRADING_E2E_GATEWAY_PORT = [string]$GatewayPort
    $env:QMT_TRADING_E2E_DATA_FOLDER = "C:\Users\nemo\lean\Lean\Data"
    $env:QMT_TRADING_E2E_LOG_PATH = $userLogPath
    $env:QMT_TRADING_E2E_ACCOUNT_ID = $accountId
    $env:QMT_TRADING_E2E_TASK_PATH = $TaskPath
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $testResult = Invoke-StreamingTestCommand $dotnetExecutable @(
        "test",
        $testProjectPath,
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--nologo",
        "--filter", "TestCategory=$TestCategory",
        "--logger", "console;verbosity=normal"
    )
    if ($testResult.ExitCode -ne 0) {
        throw "The real QMT trading E2E test failed."
    }
    $discoveryMatch = [regex]::Match($testResult.Output, "NUnit3TestExecutor discovered (?<count>\d+) of")
    if (-not $discoveryMatch.Success -or [int]$discoveryMatch.Groups["count"].Value -lt 1) {
        throw "No QMT trading E2E cases were discovered for category $TestCategory."
    }
    $discoveredTestCases = [int]$discoveryMatch.Groups["count"].Value
    $evidenceText = Get-Content -LiteralPath $userLogPath -Raw
    $completedTestCases = [regex]::Matches($evidenceText, "stage=case-complete status=ok").Count
    $skippedTestCases = [regex]::Matches($evidenceText, "stage=case status=skipped").Count
    if ($completedTestCases + $skippedTestCases -ne $discoveredTestCases) {
        throw "Expected evidence for $discoveredTestCases QMT trading E2E cases, found $completedTestCases completed and $skippedTestCases skipped."
    }
    if ($RequireCompleted -and $completedTestCases -ne $discoveredTestCases) {
        throw "Category $TestCategory requires all $discoveredTestCases cases to run; $skippedTestCases were skipped."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok tests=$discoveredTestCases passed=$completedTestCases skipped=$skippedTestCases"
    Write-TradingEvidence "[qmt-trading-e2e] stage=run status=ok log=http://192.168.50.135:8000/e2e/$LogFileName"
}
catch {
    $reason = $_.Exception.Message.Replace('"', "'").Replace("`r", " ").Replace("`n", " ")
    Write-TradingEvidence "[qmt-trading-e2e] stage=run status=failed failed_stage=$currentStage reason=`"$reason`""
    throw
}
finally {
    Remove-Item Env:QMT_TRADING_E2E_GATEWAY_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_GATEWAY_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_DATA_FOLDER -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_LOG_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_ACCOUNT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_TASK_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
}
