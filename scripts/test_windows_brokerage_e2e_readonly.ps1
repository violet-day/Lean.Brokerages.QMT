param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$LeanConfigurationPath = "C:\Users\nemo\lean_project\lean-qmt.json",
    [string]$LogRootPath = "C:\Users\nemo\lean_logs",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage",
    [int]$GatewayPort = 17890,
    [int]$LogServerPort = 8000,
    [string]$TaskPath = "test-readonly > readonly-e2e"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
. (Join-Path $PSScriptRoot "windows_build_cache.ps1")

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

    $line = "$(Get-Date -Format o) $Message"
    [System.IO.File]::AppendAllText($userLogPath, $line + "`r`n", $utf8Encoding)
    [Console]::Error.WriteLine($line)
}

function Write-CurrentTask {
    param([string]$CurrentTask)

    Write-E2EEvidence "[qmt-task] $TaskPath > $CurrentTask"
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
Write-E2EEvidence "[qmt-e2e] stage=run status=start operations=readonly"
try {
    Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=start"
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
    Write-E2EEvidence "[qmt-e2e] stage=preflight status=ok gateway_port=$GatewayPort operations=readonly"

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
        Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=miss reason=$($buildCacheState.BuildCacheMissReason) fingerprint=$($buildCacheState.BuildFingerprint)"
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
    Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=hit action=skip-build fingerprint=$($buildCacheState.BuildFingerprint) dll_sha256=$($buildCacheState.PackagedAssemblyHash)"
    $testProjectPath = $buildCacheState.TestProjectPath

    $currentStage = "brokerage-test"
    Write-CurrentTask "brokerage-test"
    Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=start"
    $env:QMT_E2E_ACCOUNT_ID = $accountId
    $env:QMT_E2E_GATEWAY_HOST = "127.0.0.1"
    $env:QMT_E2E_GATEWAY_PORT = [string]$GatewayPort
    $env:QMT_E2E_DATA_FOLDER = "C:\Users\nemo\lean\Lean\Data"
    $env:QMT_E2E_LOG_PATH = $userLogPath
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $testResult = Invoke-CapturedCommand $dotnetExecutable @(
        "test",
        $testProjectPath,
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--nologo",
        "--filter", "TestCategory=QmtReadOnlyE2E",
        "--logger", "console;verbosity=normal"
    )
    [System.IO.File]::AppendAllText($privateLogPath, $testResult.Output, $utf8Encoding)
    if ($testResult.ExitCode -ne 0) {
        $failureDetail = @($testResult.Output -split "`r?`n" | Where-Object {
            $_ -match "System\.[A-Za-z]+Exception|Expected:|But was:"
        } | Select-Object -First 1)
        if ($failureDetail.Count -ne 0) {
            Write-E2EEvidence "[qmt-e2e] stage=diagnostic status=failed detail=$($failureDetail[0].Trim())"
        }
        throw "The real QMT read-only Brokerage E2E test failed."
    }
    if (-not $testResult.Output.Contains("stage=complete status=ok operations=readonly")) {
        throw "The QMT E2E completion evidence is missing."
    }
    Write-E2EEvidence "[qmt-e2e] stage=brokerage-test status=ok tests=1"

    $currentStage = "log-server-local"
    Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=start"
    $logResponse = Invoke-WebRequest `
        -UseBasicParsing `
        -Uri "http://127.0.0.1:$LogServerPort/e2e/qmt-readonly-e2e.log" `
        -TimeoutSec 5
    if ([int]$logResponse.StatusCode -ne 200 -or
        -not ([string]$logResponse.Headers."Content-Type").StartsWith("text/plain")) {
        throw "Windows Nginx did not expose the E2E evidence as plain text."
    }
    Write-E2EEvidence "[qmt-e2e] stage=$currentStage status=ok local_url=http://127.0.0.1:$LogServerPort/e2e/qmt-readonly-e2e.log"
    Write-E2EEvidence "[qmt-e2e] stage=run status=ok log=http://192.168.50.135:$LogServerPort/e2e/qmt-readonly-e2e.log"
}
catch {
    $reason = $_.Exception.Message.Replace('"', "'").Replace("`r", " ").Replace("`n", " ")
    Write-E2EEvidence "[qmt-e2e] stage=run status=failed failed_stage=$currentStage reason=`"$reason`""
    throw
}
finally {
    Remove-Item Env:QMT_E2E_ACCOUNT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_GATEWAY_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_GATEWAY_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_DATA_FOLDER -ErrorAction SilentlyContinue
    Remove-Item Env:QMT_E2E_LOG_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_CLI_UI_LANGUAGE -ErrorAction SilentlyContinue
}
