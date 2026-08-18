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

function Get-QmtWindowsBuildCacheState {
    param(
        [string]$RepositoryPath,
        [string]$ModuleRoot,
        [string]$LeanVersion,
        [string]$TargetFramework,
        [string]$DotnetVersion
    )

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

    Push-Location $RepositoryPath
    try {
        $trackedBuildInputs = @(& $gitExecutable ls-files -s -- "QuantConnect.QmtBrokerage" "QuantConnect.QmtBrokerage.Tests" "global.json")
        if ($LASTEXITCODE -ne 0 -or $trackedBuildInputs.Count -eq 0) {
            throw "Could not determine the tracked QMT build inputs."
        }
    }
    finally {
        Pop-Location
    }

    $buildFingerprintInput = @(
        "schema_version=1"
        "lean_commit=$leanCommit"
        "lean_version=$LeanVersion"
        "target_framework=$TargetFramework"
        "dotnet_version=$DotnetVersion"
        $trackedBuildInputs
    ) -join "`n"
    $buildFingerprint = Get-TextSha256 $buildFingerprintInput

    $testProjectPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage.Tests\QuantConnect.QmtBrokerage.Tests.csproj"
    $testAssemblyPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage.Tests\bin\Release\QuantConnect.Brokerages.Qmt.Tests.dll"
    $brokerageAssemblyPath = Join-Path $RepositoryPath "QuantConnect.QmtBrokerage\bin\Release\QuantConnect.Brokerages.Qmt.dll"
    $moduleDirectory = Join-Path (Join-Path $ModuleRoot $LeanVersion) $TargetFramework
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

    return [PSCustomObject]@{
        IsBuildCacheHit = $isBuildCacheHit
        BuildCacheMissReason = $buildCacheMissReason
        BuildFingerprint = $buildFingerprint
        PackagedAssemblyHash = $packagedAssemblyHash
        LeanCommit = $leanCommit
        LeanVersion = $LeanVersion
        TargetFramework = $TargetFramework
        DotnetVersion = $DotnetVersion
        TestProjectPath = $testProjectPath
        TestAssemblyPath = $testAssemblyPath
        BrokerageAssemblyPath = $brokerageAssemblyPath
        ModuleDirectory = $moduleDirectory
        PackagedAssemblyPath = $packagedAssemblyPath
        BuildManifestPath = $buildManifestPath
    }
}

function Publish-QmtWindowsBuildCache {
    param(
        [PSCustomObject]$BuildCacheState,
        [System.Text.Encoding]$TextEncoding
    )

    if (-not (Test-Path -LiteralPath $BuildCacheState.BrokerageAssemblyPath)) {
        throw "The QMT Brokerage build output is missing: $($BuildCacheState.BrokerageAssemblyPath)"
    }
    New-Item -ItemType Directory -Path $BuildCacheState.ModuleDirectory -Force | Out-Null
    Copy-Item -LiteralPath $BuildCacheState.BrokerageAssemblyPath -Destination $BuildCacheState.ModuleDirectory -Force
    $brokerageSymbolsPath = [System.IO.Path]::ChangeExtension($BuildCacheState.BrokerageAssemblyPath, ".pdb")
    if (Test-Path -LiteralPath $brokerageSymbolsPath) {
        Copy-Item -LiteralPath $brokerageSymbolsPath -Destination $BuildCacheState.ModuleDirectory -Force
    }

    $packagedAssemblyHash = (Get-FileHash -LiteralPath $BuildCacheState.PackagedAssemblyPath -Algorithm SHA256).Hash
    $buildManifest = [ordered]@{
        schema_version = 1
        build_fingerprint = $BuildCacheState.BuildFingerprint
        dll_sha256 = $packagedAssemblyHash
        lean_commit = $BuildCacheState.LeanCommit
        lean_version = $BuildCacheState.LeanVersion
        target_framework = $BuildCacheState.TargetFramework
        dotnet_version = $BuildCacheState.DotnetVersion
        tests_passed = $true
    }
    $temporaryBuildManifestPath = "$($BuildCacheState.BuildManifestPath).tmp"
    [System.IO.File]::WriteAllText(
        $temporaryBuildManifestPath,
        ($buildManifest | ConvertTo-Json) + "`r`n",
        $TextEncoding)
    Move-Item -LiteralPath $temporaryBuildManifestPath -Destination $BuildCacheState.BuildManifestPath -Force
    return $packagedAssemblyHash
}
