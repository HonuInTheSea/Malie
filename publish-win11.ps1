param(
    [string]$Runtime = "win-x64",
    [switch]$SkipUiBuild,
    [switch]$SkipClean
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "Malie.csproj"
$publishDir = Join-Path $PSScriptRoot "publish\$Runtime"
$uiShellPath = Join-Path $PSScriptRoot "ui-shell"
$uiShellDistPath = Join-Path $uiShellPath "dist\ui-shell\browser"
$binDir = Join-Path $PSScriptRoot "bin"
$objDir = Join-Path $PSScriptRoot "obj"

Write-Host "Publishing $projectPath"
Write-Host "Output: $publishDir"

if (-not $SkipUiBuild) {
    if (-not (Test-Path $uiShellPath)) {
        throw "UI shell path not found: $uiShellPath"
    }

    Write-Host "Building Angular UI shell in $uiShellPath ..."

    if (-not (Test-Path (Join-Path $uiShellPath "node_modules"))) {
        Write-Host "node_modules not found. Installing UI dependencies..."
        Push-Location $uiShellPath
        try {
            npm ci
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
    }

    if (Test-Path $uiShellDistPath) {
        Write-Host "Clearing previous Angular dist output: $uiShellDistPath"
        Remove-Item -Path $uiShellDistPath -Recurse -Force
    }

    Push-Location $uiShellPath
    try {
        npm run build -- --configuration production
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path $uiShellDistPath)) {
        throw "Angular dist output not found after build: $uiShellDistPath"
    }

    Write-Host "Angular UI build complete."
}
else {
    Write-Host "Skipping Angular UI build by request (-SkipUiBuild)."
    if (-not (Test-Path $uiShellDistPath)) {
        throw "Angular dist output not found and -SkipUiBuild was used: $uiShellDistPath"
    }
}

if (Test-Path $publishDir) {
    Write-Host "Clearing existing publish output: $publishDir"
    Remove-Item -Path $publishDir -Recurse -Force
}

if (-not $SkipClean) {
    Write-Host "Cleaning .NET build outputs to avoid stale hashed Angular artifact references..."
    if (Test-Path $binDir) {
        Remove-Item -Path $binDir -Recurse -Force
    }
    if (Test-Path $objDir) {
        Remove-Item -Path $objDir -Recurse -Force
    }

    dotnet clean $projectPath -c Release -r $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet clean failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "Skipping clean by request (-SkipClean)."
}

$runningProcess = Get-Process Malie -ErrorAction SilentlyContinue
if ($runningProcess) {
    Write-Host "Stopping running Mâlie process(es) so publish can replace the EXE..."
    $runningProcess | Stop-Process -Force
}

dotnet publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Publish complete:"
Write-Host "  $publishDir\Malie.exe"
