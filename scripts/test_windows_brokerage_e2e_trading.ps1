param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanConfigurationPath = "C:\Users\nemo\lean_project\lean-qmt.json",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [int]$GatewayPort = 17890
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$expectedSimulationAccountId = "86033767"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

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
Write-TradingEvidence "[qmt-trading-e2e] stage=run status=start account_confirmation=simulation stock_code=600000.SH quantity=100 limit_price=automatic"
try {
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    if (-not (Test-Path -LiteralPath $LeanConfigurationPath)) {
        throw "The QMT LEAN configuration is missing: $LeanConfigurationPath"
    }
    $configuration = Get-Content -LiteralPath $LeanConfigurationPath -Raw | ConvertFrom-Json
    $configuredAccountId = [string]$configuration."qmt-account-id"
    if ($configuredAccountId -ne $expectedSimulationAccountId) {
        throw "The configured QMT account is not the fixed simulation account used by this test."
    }
    if ([string]$configuration."qmt-trading-enabled" -ne "true") {
        throw "qmt-trading-enabled must be true for the explicit simulation-account trading test."
    }
    $gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue
    if (-not $gatewayListener) {
        throw "The real QMT Gateway is not listening on Windows port $GatewayPort."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok account_match=true lean_trading_enabled=true gateway_port=$GatewayPort"

    $currentStage = "build"
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    $dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
        throw ".NET SDK is missing: $dotnetExecutable"
    }
    $testProjectPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage.Tests\QuantConnect.QmtBrokerage.Tests.csproj"
    $buildResult = Invoke-CapturedCommand $dotnetExecutable @(
        "build",
        $testProjectPath,
        "--configuration", "Release",
        "--nologo",
        "--verbosity", "minimal",
        "--disable-build-servers",
        "-nodeReuse:false",
        "-p:UseSharedCompilation=false"
    )
    [System.IO.File]::AppendAllText($privateLogPath, $buildResult.Output, $utf8Encoding)
    if ($buildResult.ExitCode -ne 0) {
        $compilerError = @($buildResult.Output -split "`r?`n" | Where-Object {
            $_ -match "error (CS|NU)[0-9]+"
        } | Select-Object -First 1)
        if ($compilerError.Count -ne 0) {
            Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=failed detail=$($compilerError[0].Trim())"
        }
        throw "The QMT trading E2E test project failed to build."
    }
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=ok errors=0"

    $currentStage = "brokerage-trading-test"
    Write-TradingEvidence "[qmt-trading-e2e] stage=$currentStage status=start"
    $env:QMT_TRADING_E2E_CONFIRMATION = "SIMULATION"
    $env:QMT_TRADING_E2E_ACCOUNT_ID = $configuredAccountId
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
        throw "The real QMT simulation-account trading E2E test failed."
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
    Remove-Item Env:QMT_TRADING_E2E_CONFIRMATION -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_ACCOUNT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_GATEWAY_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_GATEWAY_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_DATA_FOLDER -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_TRADING_E2E_LOG_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
}
