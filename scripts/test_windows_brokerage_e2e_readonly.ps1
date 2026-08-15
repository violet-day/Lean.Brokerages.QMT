param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanConfigurationPath = "C:\Users\nemo\lean_project\lean-qmt.json",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [int]$GatewayPort = 17890,
    [int]$LogServerPort = 8000
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

$privateLogDirectory = Join-Path $RepositoryPath ".test-logs"
$privateLogPath = Join-Path $privateLogDirectory "windows-brokerage-e2e-readonly-full.log"
$userLogDirectory = Join-Path $LogRootPath "e2e"
$userLogPath = Join-Path $userLogDirectory "qmt-readonly-e2e.log"
New-Item -ItemType Directory -Path $privateLogDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $userLogDirectory -Force | Out-Null
[System.IO.File]::WriteAllText($privateLogPath, "", $utf8Encoding)
[System.IO.File]::WriteAllText($userLogPath, "", $utf8Encoding)

function Write-E2EEvidence {
    param([string]$Message)

    [System.IO.File]::AppendAllText($userLogPath, $Message + "`r`n", $utf8Encoding)
    [Console]::Error.WriteLine($Message)
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

try {
    if (-not (Test-Path -LiteralPath $LeanConfigurationPath)) {
        throw "The QMT LEAN configuration is missing: $LeanConfigurationPath"
    }
    $configuration = Get-Content -LiteralPath $LeanConfigurationPath -Raw | ConvertFrom-Json
    $accountId = [string]$configuration."qmt-account-id"
    if (-not $accountId) {
        throw "qmt-account-id is missing from $LeanConfigurationPath"
    }
    if ([string]$configuration."qmt-trading-enabled" -ne "false") {
        throw "qmt-trading-enabled must be false for the read-only E2E test."
    }

    $gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $GatewayPort -ErrorAction SilentlyContinue
    if (-not $gatewayListener) {
        throw "The real QMT Gateway is not listening on Windows port $GatewayPort."
    }
    Write-E2EEvidence "[qmt-e2e] stage=preflight status=ok gateway_port=$GatewayPort trading=disabled"

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
        throw "The QMT E2E test project failed to build."
    }
    Write-E2EEvidence "[qmt-e2e] stage=build status=ok errors=0"

    $env:QMT_E2E_ACCOUNT_ID = $accountId
    $env:QMT_E2E_GATEWAY_HOST = "127.0.0.1"
    $env:QMT_E2E_GATEWAY_PORT = [string]$GatewayPort
    $testResult = Invoke-CapturedCommand $dotnetExecutable @(
        "test",
        $testProjectPath,
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--nologo",
        "--filter", "FullyQualifiedName~QmtReadOnlyE2ETests",
        "--logger", "console;verbosity=normal"
    )
    [System.IO.File]::AppendAllText($privateLogPath, $testResult.Output, $utf8Encoding)
    foreach ($evidenceLine in @($testResult.Output -split "`r?`n" | Where-Object { $_ -match "\[qmt-e2e\]" })) {
        Write-E2EEvidence $evidenceLine.Trim()
    }
    if ($testResult.ExitCode -ne 0) {
        throw "The real QMT read-only Brokerage E2E test failed."
    }
    if (-not $testResult.Output.Contains("stage=complete status=ok trading=disabled")) {
        throw "The QMT E2E completion evidence is missing."
    }
    Write-E2EEvidence "[qmt-e2e] stage=brokerage-test status=ok tests=1"

    $logResponse = Invoke-WebRequest `
        -UseBasicParsing `
        -Uri "http://127.0.0.1:$LogServerPort/e2e/qmt-readonly-e2e.log" `
        -TimeoutSec 5
    if ([int]$logResponse.StatusCode -ne 200 -or
        -not ([string]$logResponse.Headers."Content-Type").StartsWith("text/plain")) {
        throw "Windows Nginx did not expose the E2E evidence as plain text."
    }
    Write-E2EEvidence "[qmt-e2e] stage=log-serving status=ok url=http://192.168.50.135:$LogServerPort/e2e/qmt-readonly-e2e.log"
}
catch {
    Write-E2EEvidence "[qmt-e2e] stage=run status=failed reason=$($_.Exception.Message)"
    throw
}
finally {
    Remove-Item Env:QMT_E2E_ACCOUNT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_GATEWAY_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_GATEWAY_PORT -ErrorAction SilentlyContinue
}
