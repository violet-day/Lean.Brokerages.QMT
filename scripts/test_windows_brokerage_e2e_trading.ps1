param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanConfigurationPath = "C:\Users\nemo\lean_project\lean-qmt.json",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage",
    [int]$GatewayPort = 17890,
    [string]$TaskPath = "test-trading > trading-e2e"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
. (Join-Path $PSScriptRoot "windows_build_cache.ps1")

$privateLogDirectory = Join-Path $RepositoryPath ".test-logs"
$privateLogPath = Join-Path $privateLogDirectory "windows-brokerage-e2e-trading-full.log"
$userLogDirectory = Join-Path $LogRootPath "e2e"
$userLogPath = Join-Path $userLogDirectory "test-trading.log"
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

function Invoke-CapturedCommand {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $processStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processStartInfo.FileName = $Executable
    $processStartInfo.Arguments = $Arguments -join " "
    $processStartInfo.WorkingDirectory = $RepositoryPath
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
    $exitCode = $process.ExitCode
    $process.Dispose()

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $standardOutput + $standardError
    }
}

$currentStage = "preflight"
Write-TradingEvidence "[qmt-trading-e2e] stage=run status=start account_source=gateway_hello stock_code=600000.SH quantity=100 limit_price=automatic"
try {
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    if (-not (Test-Path -LiteralPath $LeanConfigurationPath)) {
        throw "The QMT LEAN configuration is missing: $LeanConfigurationPath"
    }
    $gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue
    if (-not $gatewayListener) {
        throw "The real QMT Gateway is not listening on Windows port $GatewayPort."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok gateway_port=$GatewayPort"

    $currentStage = "build-cache"
    Write-CurrentTask "csharp-build"
    $dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
        throw ".NET SDK is missing: $dotnetExecutable"
    }
    $dotnetVersion = & $dotnetExecutable --version
    $dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
    $engineImageMetadata = (& $dockerExecutable image inspect $EngineImage | ConvertFrom-Json)[0]
    $targetFramework = [string]$engineImageMetadata.Config.Labels.target_framework
    $leanVersion = [string]$engineImageMetadata.Config.Labels.lean_version
    if (-not $targetFramework -or -not $leanVersion) {
        throw "The LEAN image does not declare lean_version and target_framework: $EngineImage"
    }
    $buildCacheState = Get-QmtWindowsBuildCacheState `
        -RepositoryPath $RepositoryPath `
        -ModuleRoot $ModuleRoot `
        -LeanVersion $leanVersion `
        -TargetFramework $targetFramework `
        -DotnetVersion $dotnetVersion
    if (-not $buildCacheState.IsBuildCacheHit) {
        Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=miss reason=$($buildCacheState.BuildCacheMissReason) fingerprint=$($buildCacheState.BuildFingerprint)"
        & (Join-Path $PSScriptRoot "test_windows.ps1") `
            -RepositoryPath $RepositoryPath `
            -EngineImage $EngineImage `
            -ModuleRoot $ModuleRoot `
            -TaskPath "$TaskPath > ensure-build" `
            -EnsurePackage
        if ($LASTEXITCODE -ne 0) {
            throw "The shared QMT build and contract tests failed."
        }
        $buildCacheState = Get-QmtWindowsBuildCacheState `
            -RepositoryPath $RepositoryPath `
            -ModuleRoot $ModuleRoot `
            -LeanVersion $leanVersion `
            -TargetFramework $targetFramework `
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
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $testResult = Invoke-CapturedCommand $dotnetExecutable @(
        "test",
        $testProjectPath,
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--nologo",
        "--filter", "FullyQualifiedName~QmtTradingE2ETests",
        "--logger", "console;verbosity=normal"
    )
    [System.IO.File]::AppendAllText($privateLogPath, $testResult.Output, $utf8Encoding)
    if ($testResult.ExitCode -ne 0) {
        throw "The real QMT trading E2E test failed."
    }
    $evidenceText = Get-Content -LiteralPath $userLogPath -Raw
    if (-not $evidenceText.Contains("stage=complete status=ok")) {
        throw "The QMT trading E2E completion evidence is missing."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok tests=1"
    Write-TradingEvidence "[qmt-trading-e2e] stage=run status=ok log=http://192.168.50.135:8000/e2e/test-trading.log"
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
    Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
}
