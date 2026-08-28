param(
    [string]$RepositoryPath = "C:\Users\nemo\lean\Lean.Brokerages.QMT",
    [string]$EngineImage = "quantconnect/lean:latest",
    [string]$ModuleRoot = "$env:USERPROFILE\.lean\modules\QmtBrokerage",
    [string]$TaskPath = "test",
    [string]$LeanVersion = "",
    [string]$TargetFramework = "",
    [switch]$EnsurePackage
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$windowsTestStartedAt = Get-Date
$utf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8Encoding
[Console]::InputEncoding = $utf8Encoding
$OutputEncoding = $utf8Encoding
. (Join-Path $PSScriptRoot "windows_build_cache.ps1")
$windowsTestLogDirectory = Join-Path $RepositoryPath ".test-logs"
New-Item -ItemType Directory -Path $windowsTestLogDirectory -Force | Out-Null
$windowsTestLockPath = Join-Path $windowsTestLogDirectory "windows-test.lock"
try {
    $windowsTestLock = [System.IO.File]::Open(
        $windowsTestLockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
}
catch {
    throw "Another Windows QMT build/test/package process is already running."
}
$windowsTestLogName = if ($EnsurePackage) { "windows-package-full.log" } else { "windows-test-full.log" }
$windowsTestLogPath = Join-Path $windowsTestLogDirectory $windowsTestLogName
[System.IO.File]::WriteAllText($windowsTestLogPath, "", $utf8Encoding)

function Write-WindowsTestLog {
    param([string]$Message)

    [System.IO.File]::AppendAllText($windowsTestLogPath, $Message + "`r`n", $utf8Encoding)
    [Console]::Error.WriteLine($Message)
}

function Write-CurrentTask {
    param([string]$CurrentTask)

    Write-WindowsTestLog "[qmt-task] $TaskPath > $CurrentTask"
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
    }
    if ($standardError) {
        [System.IO.File]::AppendAllText($windowsTestLogPath, $standardError, $utf8Encoding)
    }

    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) {
        if ($standardOutput) {
            [Console]::Error.Write($standardOutput)
        }
        if ($standardError) {
            [Console]::Error.Write($standardError)
        }
    }
    $process.Dispose()
    return $exitCode
}

Push-Location $RepositoryPath
try {
    Write-WindowsTestLog "[qmt-task] $TaskPath"
    $calendarResourcePath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage\Resources\china-trading-calendar.json"
    $calendarResource = Get-Content -LiteralPath $calendarResourcePath -Raw | ConvertFrom-Json
    $calendarCoverageStart = [DateTime]::ParseExact(
        [string]$calendarResource.coverage_start,
        "yyyy-MM-dd",
        [Globalization.CultureInfo]::InvariantCulture).Date
    $calendarCoverageEnd = [DateTime]::ParseExact(
        [string]$calendarResource.coverage_end,
        "yyyy-MM-dd",
        [Globalization.CultureInfo]::InvariantCulture).Date
    $currentCalendarDate = (Get-Date).Date
    if ($currentCalendarDate -lt $calendarCoverageStart -or $currentCalendarDate -gt $calendarCoverageEnd) {
        throw "The QMT trading calendar does not cover $($currentCalendarDate.ToString('yyyy-MM-dd')); available range is $($calendarCoverageStart.ToString('yyyy-MM-dd')) through $($calendarCoverageEnd.ToString('yyyy-MM-dd'))."
    }
    Write-WindowsTestLog "[qmt-test] host=windows stage=calendar status=ok source=$($calendarResource.source) coverage_start=$($calendarCoverageStart.ToString('yyyy-MM-dd')) coverage_end=$($calendarCoverageEnd.ToString('yyyy-MM-dd')) current_date=$($currentCalendarDate.ToString('yyyy-MM-dd'))"

    if ([bool]$LeanVersion -ne [bool]$TargetFramework) {
        throw "LeanVersion and TargetFramework must be supplied together."
    }
    if (-not $LeanVersion) {
        $latestBuildManifestFile = Get-ChildItem -LiteralPath $ModuleRoot -Filter build-manifest.json -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($latestBuildManifestFile) {
            $latestBuildManifest = Get-Content -LiteralPath $latestBuildManifestFile.FullName -Raw | ConvertFrom-Json
            $LeanVersion = [string]$latestBuildManifest.lean_version
            $TargetFramework = [string]$latestBuildManifest.target_framework
            if (-not $LeanVersion -or -not $TargetFramework) {
                throw "The latest QMT build manifest does not declare its LEAN version and target framework: $($latestBuildManifestFile.FullName)"
            }
            Write-WindowsTestLog "[qmt-test] host=windows stage=engine-image status=skipped reason=verified-build-manifest lean_version=$LeanVersion target_framework=$TargetFramework"
        }
        else {
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
            $TargetFramework = [string]$engineImageMetadata.Config.Labels.target_framework
            $LeanVersion = [string]$engineImageMetadata.Config.Labels.lean_version
            if (-not $TargetFramework) {
                throw "The default LEAN image does not declare target_framework: $EngineImage"
            }
            if (-not $LeanVersion) {
                throw "The default LEAN image does not declare lean_version: $EngineImage"
            }
            Write-WindowsTestLog "[qmt-test] host=windows stage=engine-image status=ok source=docker image=$EngineImage lean_version=$LeanVersion target_framework=$TargetFramework"
        }
    }
    else {
        Write-WindowsTestLog "[qmt-test] host=windows stage=engine-image status=ok source=explicit lean_version=$LeanVersion target_framework=$TargetFramework"
    }

    if (-not $EnsurePackage) {
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

        Write-CurrentTask "python-tests"
        $pythonTestsStartedAt = Get-Date
        Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=start command=`"$pythonExecutable -m unittest discover -s tests -v`""
        $commandExitCode = Invoke-WindowsTestCommand $pythonExecutable @("-m", "unittest", "discover", "-s", "tests", "-v")
        if ($commandExitCode -ne 0) {
            Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=failed exit_code=$commandExitCode"
            throw "Python tests failed with exit code $commandExitCode."
        }
        $pythonTestsDurationMilliseconds = [int]((Get-Date) - $pythonTestsStartedAt).TotalMilliseconds
        Write-WindowsTestLog "[qmt-test] host=windows stage=python-tests status=ok duration_ms=$pythonTestsDurationMilliseconds"
    }

    $dotnetExecutable = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
        throw ".NET 10 SDK is missing: $dotnetExecutable"
    }
    $dotnetVersion = & $dotnetExecutable --version
    if (-not $dotnetVersion.StartsWith("10.")) {
        throw "Expected .NET 10 SDK, found $dotnetVersion."
    }

    $buildCacheState = Get-QmtWindowsBuildCacheState `
        -RepositoryPath $RepositoryPath `
        -ModuleRoot $ModuleRoot `
        -LeanVersion $LeanVersion `
        -TargetFramework $TargetFramework `
        -DotnetVersion $dotnetVersion
    $testProjectPath = $buildCacheState.TestProjectPath

    Write-CurrentTask "csharp-build"
    if ($buildCacheState.IsBuildCacheHit) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-cache status=hit action=skip-build fingerprint=$($buildCacheState.BuildFingerprint) dll_sha256=$($buildCacheState.PackagedAssemblyHash)"
    }
    else {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-cache status=miss reason=$($buildCacheState.BuildCacheMissReason) fingerprint=$($buildCacheState.BuildFingerprint)"
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-server status=start action=shutdown"
        & $dotnetExecutable build-server shutdown 2>&1 | ForEach-Object {
            Write-WindowsTestLog ([string]$_)
        }
        if ($LASTEXITCODE -ne 0) {
            throw ".NET build-server shutdown failed with exit code $LASTEXITCODE."
        }
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-server status=ok"

        $dotnetBuildStartedAt = Get-Date
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=start dotnet=$dotnetVersion target_framework=$TargetFramework project=$testProjectPath"
        $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("build", $testProjectPath, "--configuration", "Release", "--nologo", "--verbosity", "minimal", "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:TargetFramework=$TargetFramework")
        if ($commandExitCode -ne 0) {
            Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=failed exit_code=$commandExitCode"
            throw ".NET build failed with exit code $commandExitCode."
        }
        $dotnetBuildDurationMilliseconds = [int]((Get-Date) - $dotnetBuildStartedAt).TotalMilliseconds
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=ok duration_ms=$dotnetBuildDurationMilliseconds"
    }

    Write-CurrentTask "csharp-tests"
    if ($buildCacheState.IsBuildCacheHit) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=skipped reason=build-cache-hit fingerprint=$($buildCacheState.BuildFingerprint)"
    }
    else {
        $dotnetTestsStartedAt = Get-Date
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=start project=$testProjectPath no_build=true"
        $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("test", $testProjectPath, "--configuration", "Release", "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=normal", "-p:TargetFramework=$TargetFramework")
        if ($commandExitCode -ne 0) {
            Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=failed exit_code=$commandExitCode"
            throw ".NET tests failed with exit code $commandExitCode."
        }
        $dotnetTestsDurationMilliseconds = [int]((Get-Date) - $dotnetTestsStartedAt).TotalMilliseconds
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=ok duration_ms=$dotnetTestsDurationMilliseconds"
    }

    Write-CurrentTask "package-dll"
    if ($buildCacheState.IsBuildCacheHit) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=package status=ok action=reuse path=$($buildCacheState.ModuleDirectory) sha256=$($buildCacheState.PackagedAssemblyHash) fingerprint=$($buildCacheState.BuildFingerprint)"
    }
    else {
        $packagedAssemblyHash = Publish-QmtWindowsBuildCache `
            -BuildCacheState $buildCacheState `
            -TextEncoding $utf8Encoding
        Write-WindowsTestLog "[qmt-test] host=windows stage=package status=ok action=update path=$($buildCacheState.ModuleDirectory) sha256=$packagedAssemblyHash fingerprint=$($buildCacheState.BuildFingerprint)"
    }

    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=all status=ok duration_ms=$totalDurationMilliseconds"
    Write-Output $buildCacheState.ModuleDirectory
}
finally {
    Pop-Location
    $windowsTestLock.Dispose()
    Remove-Item -LiteralPath $windowsTestLockPath -Force -ErrorAction SilentlyContinue
}
