param(
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [switch]$AcceptWixEula,
    [switch]$SkipUiBuild,
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$publishScript = Join-Path $projectRoot "publish-win11.ps1"
$publishDir = Join-Path $projectRoot "publish\$Runtime"
$publishExe = Join-Path $publishDir "Malie.exe"
$installerRoot = Join-Path $projectRoot "installer"
$distDir = Join-Path $installerRoot "dist"
$innoScript = Join-Path $installerRoot "inno\setup.iss"
$wixProduct = Join-Path $installerRoot "wix\Product.wxs"
$wixFragmentScript = Join-Path $installerRoot "wix\New-WixFragment.ps1"
$wixGeneratedFragment = Join-Path $installerRoot "wix\InstallerFiles.generated.wxs"
$msiOutput = Join-Path $distDir "Malie-$Version-$Runtime.msi"
$appSunIcon = Join-Path $projectRoot "Assets\Icons\app-sun-3d.ico"

function Resolve-InnoCompilerPath {
    param(
        [string]$CandidatePath
    )

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($CandidatePath).Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($expanded)) {
        return $null
    }

    if (Test-Path $expanded -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($expanded).Equals(".exe", [StringComparison]::OrdinalIgnoreCase)) {
            return (Resolve-Path $expanded).Path
        }
        return $null
    }

    if (Test-Path $expanded -PathType Container) {
        $isccFromDir = Join-Path $expanded "ISCC.exe"
        if (Test-Path $isccFromDir -PathType Leaf) {
            return (Resolve-Path $isccFromDir).Path
        }
        return $null
    }

    if ([string]::IsNullOrWhiteSpace([System.IO.Path]::GetExtension($expanded))) {
        $asExe = "$expanded.exe"
        if (Test-Path $asExe -PathType Leaf) {
            return (Resolve-Path $asExe).Path
        }
    }

    return $null
}

if (-not (Test-Path $publishScript)) {
    throw "publish-win11.ps1 not found: $publishScript"
}
if (-not (Test-Path $innoScript)) {
    throw "Inno script not found: $innoScript"
}
if (-not (Test-Path $wixProduct)) {
    throw "WiX product source not found: $wixProduct"
}
if (-not (Test-Path $wixFragmentScript)) {
    throw "WiX fragment generator not found: $wixFragmentScript"
}
if (-not (Test-Path $appSunIcon)) {
    throw "Sun icon not found: $appSunIcon"
}

New-Item -Path $distDir -ItemType Directory -Force | Out-Null

Write-Host "Publishing app artifacts for runtime '$Runtime'..."
$publishArgs = @{
    Runtime = $Runtime
}
if ($SkipUiBuild) {
    $publishArgs["SkipUiBuild"] = $true
}
if ($SkipClean) {
    $publishArgs["SkipClean"] = $true
}
& $publishScript @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "publish-win11.ps1 failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $publishExe)) {
    throw "Publish output EXE not found: $publishExe"
}

Write-Host "Building Inno Setup EXE installer..."
$innoCompiler = Resolve-InnoCompilerPath -CandidatePath $env:INNO_COMPILER
if (-not $innoCompiler) {
    $candidatePaths = @(
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidatePaths) {
        $resolvedCandidate = Resolve-InnoCompilerPath -CandidatePath $candidate
        if ($resolvedCandidate) {
            $innoCompiler = $resolvedCandidate
            break
        }
    }
}

if (-not $innoCompiler) {
    throw "Inno Setup compiler not found. Install Inno Setup 7 or set INNO_COMPILER to ISCC.exe or its install folder."
}

Write-Host "Using Inno compiler: $innoCompiler"

& $innoCompiler `
    "/DMyAppVersion=$Version" `
    "/DMyPublishDir=$publishDir" `
    "/DMyOutputDir=$distDir" `
    $innoScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno setup build failed with exit code $LASTEXITCODE."
}

Write-Host "Generating WiX file manifest..."
& $wixFragmentScript -PublishDir $publishDir -OutputPath $wixGeneratedFragment
if ($LASTEXITCODE -ne 0) {
    throw "WiX fragment generation failed with exit code $LASTEXITCODE."
}

Write-Host "Building WiX MSI installer..."
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
$wixExePath = if ($wixCmd) { $wixCmd.Path } else { $null }
if (-not $wixExePath) {
    $wixCandidates = @(
        (Join-Path $env:ProgramFiles "WiX Toolset v7.0\bin\wix.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "WiX Toolset v4\bin\wix.exe")
    )
    foreach ($candidate in $wixCandidates) {
        if (Test-Path $candidate) {
            $wixExePath = $candidate
            break
        }
    }
}

if (-not $wixExePath) {
    throw "WiX CLI not found. Install WiX Toolset v7.0 and ensure 'wix' is on PATH."
}

$wixWorkingDir = Split-Path -Parent $wixProduct
$resolvedAppSunIcon = (Resolve-Path $appSunIcon).Path
$wixHelpText = (& $wixExePath --help 2>&1 | Out-String)
$supportsAcceptEulaLongFlag = $wixHelpText -match '--acceptEula'
$supportsAcceptEulaShortFlag = $wixHelpText -match '-acceptEula'

if ($AcceptWixEula) {
    Write-Host "Accepting WiX OSMF EULA for current user..."
    & $wixExePath eula accept wix7
    if ($LASTEXITCODE -ne 0) {
        throw "WiX EULA acceptance failed with exit code $LASTEXITCODE."
    }
}

function New-WixBuildArgs {
    param(
        [switch]$UseInvocationEula,
        [ValidateSet("auto", "long", "short")]
        [string]$EulaArgStyle = "auto"
    )

    $args = @()
    if ($UseInvocationEula) {
        $resolvedStyle = $EulaArgStyle
        if ($resolvedStyle -eq "auto") {
            if ($supportsAcceptEulaShortFlag) {
                $resolvedStyle = "short"
            } elseif ($supportsAcceptEulaLongFlag) {
                $resolvedStyle = "long"
            } else {
                $resolvedStyle = "short"
            }
        }

        if ($resolvedStyle -eq "long") {
            $args += @("--acceptEula", "build")
        } else {
            $args += @("build", "-acceptEula", "wix7")
        }
    } else {
        $args += "build"
    }

    $args += @(
        "Product.wxs",
        "InstallerFiles.generated.wxs",
        "-arch", "x64",
        "-ext", "WixToolset.Util.wixext",
        "-d", "AppSunIconPath=$resolvedAppSunIcon",
        "-d", "ProductVersion=$Version",
        "-o", $msiOutput
    )

    return ,$args
}

function Invoke-WixBuild {
    param(
        [string]$ExecutablePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        $output = & $ExecutablePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    return [pscustomobject]@{
        Output   = $output
        ExitCode = $exitCode
    }
}

$wixArgs = New-WixBuildArgs -UseInvocationEula:$AcceptWixEula
$wixBuildResult = Invoke-WixBuild -ExecutablePath $wixExePath -Arguments $wixArgs -WorkingDirectory $wixWorkingDir
$wixBuildResult.Output | ForEach-Object { Write-Host $_ }

if ($wixBuildResult.ExitCode -ne 0) {
    $joinedOutput = ($wixBuildResult.Output | Out-String)
    if ($joinedOutput -match 'WIX7015') {
        Write-Host "WiX OSMF EULA not accepted. Accepting now and retrying MSI build..."
        & $wixExePath eula accept wix7
        if ($LASTEXITCODE -ne 0) {
            throw "WiX EULA acceptance failed with exit code $LASTEXITCODE. Run manually: `"$wixExePath`" eula accept wix7. See https://docs.firegiant.com/wix/osmf/"
        }

        $retryStyles = @("short", "long", "auto")
        foreach ($style in $retryStyles) {
            $wixArgs = New-WixBuildArgs -UseInvocationEula -EulaArgStyle $style
            $wixBuildResult = Invoke-WixBuild -ExecutablePath $wixExePath -Arguments $wixArgs -WorkingDirectory $wixWorkingDir
            $wixBuildResult.Output | ForEach-Object { Write-Host $_ }
            if ($wixBuildResult.ExitCode -eq 0) {
                break
            }
        }
    }
}

if ($wixBuildResult.ExitCode -ne 0) {
    throw "WiX MSI build failed with exit code $($wixBuildResult.ExitCode). If this is OSMF/EULA related, review https://docs.firegiant.com/wix/osmf/ and run `"$wixExePath`" eula accept wix7 or re-run this script with -AcceptWixEula."
}

Write-Host ""
Write-Host "Installer build complete."
Write-Host "EXE and MSI output folder:"
Write-Host "  $distDir"
