param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$windowsTestStartedAt = Get-Date
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
[Console]::InputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
$windowsTestLogPath = Join-Path $RepositoryPath ".test-logs\windows-test-full.log"
New-Item -ItemType Directory -Path (Split-Path -Parent $windowsTestLogPath) -Force | Out-Null
[System.IO.File]::WriteAllText($windowsTestLogPath, "", $utf8Encoding)

function Write-WindowsTestLog {
    param([string]$Message)

    [System.IO.File]::AppendAllText($windowsTestLogPath, $Message + "`r`n", $utf8Encoding)
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
        [System.IO.File]::AppendAllText($windowsTestLogPath, $standardOutput, $utf8Encoding)
        [Console]::Error.Write($standardOutput)
    }
    if ($standardError) {
        [System.IO.File]::AppendAllText($windowsTestLogPath, $standardError, $utf8Encoding)
        [Console]::Error.Write($standardError)
    }

    $exitCode = $process.ExitCode
    $process.Dispose()
    return $exitCode
}

Push-Location $RepositoryPath
try {
    $dockerExecutable = (Get-Command docker.exe -ErrorAction Stop).Source
    & $dockerExecutable image inspect $EngineImage 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=engine-image status=start action=pull image=$EngineImage"
        & $dockerExecutable pull $EngineImage
        if ($LASTEXITCODE -ne 0) {
            throw "Could not pull the default LEAN image $EngineImage."
        }
    }

    $engineImageMetadata = (& $dockerExecutable image inspect $EngineImage | ConvertFrom-Json)[0]
    $targetFramework = [string]$engineImageMetadata.Config.Labels.target_framework
    $leanVersion = [string]$engineImageMetadata.Config.Labels.lean_version
    if (-not $targetFramework) {
        throw "The default LEAN image does not declare target_framework: $EngineImage"
    }
    if (-not $leanVersion) {
        throw "The default LEAN image does not declare lean_version: $EngineImage"
    }
    Write-WindowsTestLog "[qmt-test] host=windows stage=engine-image status=ok image=$EngineImage lean_version=$leanVersion target_framework=$targetFramework"

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
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=start dotnet=$dotnetVersion target_framework=$targetFramework command=`"$dotnetExecutable build QuantConnect.QmtBrokerage.sln --configuration Release`""
    $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("build", ".\QuantConnect.QmtBrokerage.sln", "--configuration", "Release", "--nologo", "--verbosity", "minimal", "-p:TargetFramework=$targetFramework")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=failed exit_code=$commandExitCode"
        throw ".NET build failed with exit code $commandExitCode."
    }
    $dotnetBuildDurationMilliseconds = [int]((Get-Date) - $dotnetBuildStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=ok duration_ms=$dotnetBuildDurationMilliseconds"

    $dotnetTestsStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=start command=`"dotnet test QuantConnect.QmtBrokerage.sln --no-build`""
    $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("test", ".\QuantConnect.QmtBrokerage.sln", "--configuration", "Release", "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=normal", "-p:TargetFramework=$targetFramework")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=failed exit_code=$commandExitCode"
        throw ".NET tests failed with exit code $commandExitCode."
    }
    $dotnetTestsDurationMilliseconds = [int]((Get-Date) - $dotnetTestsStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=ok duration_ms=$dotnetTestsDurationMilliseconds"

    $brokerageAssemblyPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage\bin\Release\QuantConnect.Brokerages.Qmt.dll"
    if (-not (Test-Path -LiteralPath $brokerageAssemblyPath)) {
        throw "The QMT Brokerage build output is missing: $brokerageAssemblyPath"
    }
    $moduleDirectory = Join-Path (Join-Path $ModuleRoot $leanVersion) $targetFramework
    New-Item -ItemType Directory -Path $moduleDirectory -Force | Out-Null
    Copy-Item -LiteralPath $brokerageAssemblyPath -Destination $moduleDirectory -Force
    $brokerageSymbolsPath = [System.IO.Path]::ChangeExtension($brokerageAssemblyPath, ".pdb")
    if (Test-Path -LiteralPath $brokerageSymbolsPath) {
        Copy-Item -LiteralPath $brokerageSymbolsPath -Destination $moduleDirectory -Force
    }
    $packagedAssemblyPath = Join-Path $moduleDirectory "QuantConnect.Brokerages.Qmt.dll"
    $packagedAssemblyHash = (Get-FileHash -LiteralPath $packagedAssemblyPath -Algorithm SHA256).Hash
    Write-WindowsTestLog "[qmt-test] host=windows stage=package status=ok lean_version=$leanVersion target_framework=$targetFramework path=$moduleDirectory sha256=$packagedAssemblyHash"

    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=all status=ok duration_ms=$totalDurationMilliseconds"
    Write-Output $moduleDirectory
}
finally {
    Pop-Location
}
