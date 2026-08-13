param(
    [string]$RepositoryPath = "C:\Users\nemo\lean-net10\Lean.Brokerages.QMT",
    [string]$LeanProjectRoot = "C:\Users\nemo\lean_project",
    [string]$ImageTag = "qmt-20260813-d72852f25-worktree",
    [int]$FakeGatewayPort = 17891
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding

function Write-DeploymentLog {
    param([string]$Message)

    [Console]::Error.WriteLine("[qmt-deploy] $Message")
}

$dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
$leanExecutable = "C:\Users\nemo\anaconda3\Scripts\lean.exe"
$pythonExecutable = Join-Path $RepositoryPath ".venv\Scripts\python.exe"
$baseConfigurationPath = Join-Path $LeanProjectRoot "lean-qmt.json"
$smokeConfigurationPath = Join-Path $LeanProjectRoot "lean-qmt-smoke.json"
$smokeProjectPath = Join-Path $LeanProjectRoot "qmt-deployment-smoke"
$fakeGatewayOutputPath = Join-Path $RepositoryPath ".test-logs\fake-gateway-output.log"
$fakeGatewayStandardOutputPath = Join-Path $RepositoryPath ".test-logs\fake-gateway-standard-output.log"
$liveOutputPath = Join-Path $RepositoryPath ".test-logs\deployment-smoke-output"
$imageName = "lean-cli/engine:$ImageTag"
$fakeAccountId = "deployment-test"

& $dockerExecutable version --format "{{.Server.Os}}/{{.Server.Arch}}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is not ready."
}
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw "The QMT repository Python environment is missing: $pythonExecutable"
}

$smokeConfiguration = Get-Content -LiteralPath $baseConfigurationPath -Raw | ConvertFrom-Json
$smokeConfiguration | Add-Member -NotePropertyName "qmt-gateway-host" -NotePropertyValue "host.docker.internal" -Force
$smokeConfiguration | Add-Member -NotePropertyName "qmt-gateway-port" -NotePropertyValue ([string]$FakeGatewayPort) -Force
$smokeConfiguration | Add-Member -NotePropertyName "qmt-account-id" -NotePropertyValue $fakeAccountId -Force
$smokeConfiguration | Add-Member -NotePropertyName "qmt-request-timeout" -NotePropertyValue "10" -Force
$smokeConfiguration | Add-Member -NotePropertyName "qmt-trading-enabled" -NotePropertyValue "false" -Force
[System.IO.File]::WriteAllText(
    $smokeConfigurationPath,
    (($smokeConfiguration | ConvertTo-Json -Depth 100) + "`n"),
    $utf8Encoding)

if (Test-Path -LiteralPath $smokeProjectPath) {
    Remove-Item -LiteralPath $smokeProjectPath -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $RepositoryPath "deployment\smoke") -Destination $smokeProjectPath -Recurse
New-Item -ItemType Directory -Path (Split-Path -Parent $fakeGatewayOutputPath) -Force | Out-Null
if (Test-Path -LiteralPath $liveOutputPath) {
    Remove-Item -LiteralPath $liveOutputPath -Recurse -Force
}

$fakeGatewayArguments = @(
    (Join-Path $RepositoryPath "scripts\fake_qmt_gateway.py"),
    "--host", "0.0.0.0",
    "--port", [string]$FakeGatewayPort,
    "--account-id", $fakeAccountId
)
Write-DeploymentLog "stage=fake-gateway status=start port=$FakeGatewayPort trading_enabled=false"
$fakeGatewayProcess = Start-Process -FilePath $pythonExecutable -ArgumentList $fakeGatewayArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $fakeGatewayStandardOutputPath -RedirectStandardError $fakeGatewayOutputPath
try {
    $gatewayDeadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $gatewayListener = Get-NetTCPConnection -State Listen -LocalPort $FakeGatewayPort -ErrorAction SilentlyContinue
    } while (-not $gatewayListener -and (Get-Date) -lt $gatewayDeadline)
    if (-not $gatewayListener) {
        throw "The fake QMT Gateway did not listen on port $FakeGatewayPort."
    }
    Write-DeploymentLog "stage=fake-gateway status=ok port=$FakeGatewayPort"

    $leanArguments = @(
        "live", "deploy", $smokeProjectPath,
        "--lean-config", $smokeConfigurationPath,
        "--environment", "live-qmt",
        "--image", $imageName,
        "--no-update",
        "--output", $liveOutputPath
    )
    Write-DeploymentLog "stage=lean-smoke status=start image=$imageName environment=live-qmt"
    & $leanExecutable @leanArguments
    if ($LASTEXITCODE -ne 0) {
        throw "lean live deploy failed with exit code $LASTEXITCODE."
    }

    $gatewayLog = Get-Content -LiteralPath $fakeGatewayOutputPath -Raw
    foreach ($expectedOperation in @("hello", "query_account", "query_positions", "query_orders", "subscribe")) {
        if (-not $gatewayLog.Contains("operation=$expectedOperation")) {
            throw "The fake Gateway did not receive $expectedOperation."
        }
    }
    if ($gatewayLog.Contains("operation=place_order") -or $gatewayLog.Contains("operation=cancel_order")) {
        throw "The deployment smoke attempted a trading operation."
    }

    $logFiles = @(Get-ChildItem -LiteralPath $liveOutputPath -Recurse -File -ErrorAction SilentlyContinue)
    $smokePassed = $false
    foreach ($logFile in $logFiles) {
        if (Select-String -LiteralPath $logFile.FullName -SimpleMatch "QMT deployment smoke received fake live data" -Quiet -ErrorAction SilentlyContinue) {
            $smokePassed = $true
            break
        }
    }
    if (-not $smokePassed) {
        throw "The LEAN smoke success marker was not found in $liveOutputPath."
    }
    Write-DeploymentLog "stage=lean-smoke status=ok image=$imageName trading_enabled=false"
}
finally {
    if ($fakeGatewayProcess -and -not $fakeGatewayProcess.HasExited) {
        Stop-Process -Id $fakeGatewayProcess.Id -Force
        $fakeGatewayProcess.WaitForExit()
    }
    Remove-Item -LiteralPath $smokeConfigurationPath -Force -ErrorAction SilentlyContinue
}
