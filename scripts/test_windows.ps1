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
$windowsTestLogPath = Join-Path $windowsTestLogDirectory "windows-test-full.log"
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

function Get-TextSha256 {
    param([string]$Text)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $textBytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($textBytes))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
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

    $gitExecutable = (Get-Command git.exe -ErrorAction Stop).Source
    $leanRepositoryPath = Join-Path (Split-Path -Parent $RepositoryPath) "Lean"
    $leanCommit = (& $gitExecutable -C $leanRepositoryPath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $leanCommit) {
        throw "Could not determine the Windows LEAN commit: $leanRepositoryPath"
    }
    $leanTrackedChanges = @(& $gitExecutable -C $leanRepositoryPath status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the Windows LEAN worktree: $leanRepositoryPath"
    }

    $trackedBuildInputs = @(& $gitExecutable ls-files -s -- "QuantConnect.QmtBrokerage" "QuantConnect.QmtBrokerage.Tests" "global.json")
    if ($LASTEXITCODE -ne 0 -or $trackedBuildInputs.Count -eq 0) {
        throw "Could not determine the tracked QMT build inputs."
    }
    $buildFingerprintInput = @(
        "schema_version=1"
        "lean_commit=$leanCommit"
        "lean_version=$leanVersion"
        "target_framework=$targetFramework"
        "dotnet_version=$dotnetVersion"
        $trackedBuildInputs
    ) -join "`n"
    $buildFingerprint = Get-TextSha256 $buildFingerprintInput

    $testProjectPath = ".\QuantConnect.QmtBrokerage.Tests\QuantConnect.QmtBrokerage.Tests.csproj"
    $testAssemblyPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage.Tests\bin\Release\QuantConnect.Brokerages.Qmt.Tests.dll"
    $brokerageAssemblyPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage\bin\Release\QuantConnect.Brokerages.Qmt.dll"
    $moduleDirectory = Join-Path (Join-Path $ModuleRoot $leanVersion) $targetFramework
    $packagedAssemblyPath = Join-Path $moduleDirectory "QuantConnect.Brokerages.Qmt.dll"
    $buildManifestPath = Join-Path $moduleDirectory "build-manifest.json"
    $isBuildCacheHit = $false
    $buildCacheMissReason = "manifest-missing"
    $packagedAssemblyHash = ""

    if ($leanTrackedChanges.Count -ne 0) {
        $buildCacheMissReason = "lean-worktree-dirty"
    }
    elseif (Test-Path -LiteralPath $buildManifestPath) {
        try {
            $buildManifest = Get-Content -LiteralPath $buildManifestPath -Raw | ConvertFrom-Json
            if ([int]$buildManifest.schema_version -ne 1) {
                $buildCacheMissReason = "manifest-version-mismatch"
            }
            elseif ([string]$buildManifest.build_fingerprint -ne $buildFingerprint) {
                $buildCacheMissReason = "fingerprint-changed"
            }
            elseif (-not [bool]$buildManifest.tests_passed) {
                $buildCacheMissReason = "tests-not-passed"
            }
            elseif (-not (Test-Path -LiteralPath $packagedAssemblyPath)) {
                $buildCacheMissReason = "module-missing"
            }
            elseif (-not (Test-Path -LiteralPath $testAssemblyPath)) {
                $buildCacheMissReason = "test-assembly-missing"
            }
            else {
                $packagedAssemblyHash = (Get-FileHash -LiteralPath $packagedAssemblyPath -Algorithm SHA256).Hash
                if ($packagedAssemblyHash -ne [string]$buildManifest.dll_sha256) {
                    $buildCacheMissReason = "module-hash-mismatch"
                }
                else {
                    $isBuildCacheHit = $true
                }
            }
        }
        catch {
            $buildCacheMissReason = "manifest-invalid"
        }
    }

    if ($isBuildCacheHit) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-cache status=hit action=skip-build fingerprint=$buildFingerprint dll_sha256=$packagedAssemblyHash"
    }
    else {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-cache status=miss reason=$buildCacheMissReason fingerprint=$buildFingerprint"
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-server status=start action=shutdown"
        & $dotnetExecutable build-server shutdown 2>&1 | ForEach-Object {
            Write-WindowsTestLog ([string]$_)
        }
        if ($LASTEXITCODE -ne 0) {
            throw ".NET build-server shutdown failed with exit code $LASTEXITCODE."
        }
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build-server status=ok"

        $dotnetBuildStartedAt = Get-Date
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=start dotnet=$dotnetVersion target_framework=$targetFramework project=$testProjectPath"
        $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("build", $testProjectPath, "--configuration", "Release", "--nologo", "--verbosity", "minimal", "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:TargetFramework=$targetFramework")
        if ($commandExitCode -ne 0) {
            Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=failed exit_code=$commandExitCode"
            throw ".NET build failed with exit code $commandExitCode."
        }
        $dotnetBuildDurationMilliseconds = [int]((Get-Date) - $dotnetBuildStartedAt).TotalMilliseconds
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-build status=ok duration_ms=$dotnetBuildDurationMilliseconds"
    }

    $dotnetTestsStartedAt = Get-Date
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=start project=$testProjectPath no_build=true"
    $commandExitCode = Invoke-WindowsTestCommand $dotnetExecutable @("test", $testProjectPath, "--configuration", "Release", "--no-build", "--no-restore", "--nologo", "--logger", "console;verbosity=normal", "-p:TargetFramework=$targetFramework")
    if ($commandExitCode -ne 0) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=failed exit_code=$commandExitCode"
        throw ".NET tests failed with exit code $commandExitCode."
    }
    $dotnetTestsDurationMilliseconds = [int]((Get-Date) - $dotnetTestsStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=dotnet-tests status=ok duration_ms=$dotnetTestsDurationMilliseconds"

    if ($isBuildCacheHit) {
        Write-WindowsTestLog "[qmt-test] host=windows stage=package status=ok action=reuse path=$moduleDirectory sha256=$packagedAssemblyHash fingerprint=$buildFingerprint"
    }
    else {
        if (-not (Test-Path -LiteralPath $brokerageAssemblyPath)) {
            throw "The QMT Brokerage build output is missing: $brokerageAssemblyPath"
        }
        New-Item -ItemType Directory -Path $moduleDirectory -Force | Out-Null
        Copy-Item -LiteralPath $brokerageAssemblyPath -Destination $moduleDirectory -Force
        $brokerageSymbolsPath = [System.IO.Path]::ChangeExtension($brokerageAssemblyPath, ".pdb")
        if (Test-Path -LiteralPath $brokerageSymbolsPath) {
            Copy-Item -LiteralPath $brokerageSymbolsPath -Destination $moduleDirectory -Force
        }
        $packagedAssemblyHash = (Get-FileHash -LiteralPath $packagedAssemblyPath -Algorithm SHA256).Hash
        $buildManifest = [ordered]@{
            schema_version = 1
            build_fingerprint = $buildFingerprint
            dll_sha256 = $packagedAssemblyHash
            lean_commit = $leanCommit
            lean_version = $leanVersion
            target_framework = $targetFramework
            dotnet_version = $dotnetVersion
            tests_passed = $true
        }
        $temporaryBuildManifestPath = "$buildManifestPath.tmp"
        [System.IO.File]::WriteAllText(
            $temporaryBuildManifestPath,
            ($buildManifest | ConvertTo-Json) + "`r`n",
            $utf8Encoding)
        Move-Item -LiteralPath $temporaryBuildManifestPath -Destination $buildManifestPath -Force
        Write-WindowsTestLog "[qmt-test] host=windows stage=package status=ok action=update path=$moduleDirectory sha256=$packagedAssemblyHash fingerprint=$buildFingerprint"
    }

    $totalDurationMilliseconds = [int]((Get-Date) - $windowsTestStartedAt).TotalMilliseconds
    Write-WindowsTestLog "[qmt-test] host=windows stage=all status=ok duration_ms=$totalDurationMilliseconds"
    Write-Output $moduleDirectory
}
finally {
    Pop-Location
    $windowsTestLock.Dispose()
    Remove-Item -LiteralPath $windowsTestLockPath -Force -ErrorAction SilentlyContinue
}
