param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "Publish directory not found: $PublishDir"
}

$resolvedPublishDir = (Resolve-Path $PublishDir).Path
$files = Get-ChildItem -Path $resolvedPublishDir -File -Recurse | Sort-Object FullName
if (-not $files -or $files.Count -eq 0) {
    throw "No files found under publish directory: $resolvedPublishDir"
}

function Normalize-RelativePath([string]$fullPath, [string]$basePath) {
    $baseFull = [System.IO.Path]::GetFullPath($basePath).TrimEnd('\', '/') + '\'
    $fileFull = [System.IO.Path]::GetFullPath($fullPath)
    $baseUri = New-Object System.Uri($baseFull)
    $fileUri = New-Object System.Uri($fileFull)
    $relativeUri = $baseUri.MakeRelativeUri($fileUri)
    $relativePath = [System.Uri]::UnescapeDataString($relativeUri.ToString())
    return $relativePath.Replace('\', '/')
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function New-SafeId([string]$prefix, [string]$seed) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($seed)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($bytes)
    }
    finally {
        $sha256.Dispose()
    }
    $hex = [BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 20)
    return "${prefix}_${hex}"
}

$directories = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$filesByDirectory = @{}
$componentEntries = @()

foreach ($file in $files) {
    $relativePath = Normalize-RelativePath $file.FullName $resolvedPublishDir
    $relativeDirectory = [System.IO.Path]::GetDirectoryName($relativePath)
    if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
        $relativeDirectory = ""
    } else {
        $relativeDirectory = $relativeDirectory.Replace('\', '/')
    }

    $null = $directories.Add($relativeDirectory)
    $dirCursor = $relativeDirectory
    while (-not [string]::IsNullOrWhiteSpace($dirCursor)) {
        $parentDir = [System.IO.Path]::GetDirectoryName($dirCursor)
        if ([string]::IsNullOrWhiteSpace($parentDir)) {
            $parentDir = ""
        } else {
            $parentDir = $parentDir.Replace('\', '/')
        }
        $null = $directories.Add($parentDir)
        $dirCursor = $parentDir
    }

    if (-not $filesByDirectory.ContainsKey($relativeDirectory)) {
        $filesByDirectory[$relativeDirectory] = New-Object System.Collections.Generic.List[object]
    }

    $componentId = New-SafeId "CMP" $relativePath
    $fileId = New-SafeId "FIL" $relativePath
    $filesByDirectory[$relativeDirectory].Add([pscustomobject]@{
            RelativePath = $relativePath
            FullPath     = $file.FullName
            ComponentId  = $componentId
            FileId       = $fileId
        })
    $componentEntries += $componentId
}

$directoryIdByRelativePath = @{
    "" = "INSTALLFOLDER"
}

foreach ($directory in ($directories | Sort-Object { $_.Length })) {
    if ($directory -eq "") {
        continue
    }
    $directoryIdByRelativePath[$directory] = New-SafeId "DIR" $directory
}

$childDirectoriesByParent = @{}
foreach ($directory in $directories) {
    if ($directory -eq "") {
        continue
    }

    $parent = [System.IO.Path]::GetDirectoryName($directory)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        $parent = ""
    } else {
        $parent = $parent.Replace('\', '/')
    }

    if (-not $childDirectoriesByParent.ContainsKey($parent)) {
        $childDirectoriesByParent[$parent] = New-Object System.Collections.Generic.List[string]
    }
    $childDirectoriesByParent[$parent].Add($directory)
}

$lines = New-Object System.Collections.Generic.List[string]
$indent = "  "

$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add("${indent}<Fragment>")
$lines.Add("${indent}${indent}<DirectoryRef Id=""INSTALLFOLDER"">")

function Write-DirectoryNodes {
    param(
        [string]$ParentRelativePath,
        [int]$Depth
    )

    $baseIndent = "  " * $Depth
    $filesInDirectory = if ($filesByDirectory.ContainsKey($ParentRelativePath)) { $filesByDirectory[$ParentRelativePath] } else { @() }
    foreach ($entry in $filesInDirectory) {
        $escapedSource = Escape-Xml $entry.FullPath
        $lines.Add("${baseIndent}<Component Id=""$($entry.ComponentId)"" Guid=""*"">")
        $lines.Add("${baseIndent}  <File Id=""$($entry.FileId)"" Source=""$escapedSource"" KeyPath=""yes"" />")
        $lines.Add("${baseIndent}</Component>")
    }

    if (-not $childDirectoriesByParent.ContainsKey($ParentRelativePath)) {
        return
    }

    foreach ($childRelativePath in ($childDirectoriesByParent[$ParentRelativePath] | Sort-Object)) {
        $directoryName = Split-Path -Path $childRelativePath -Leaf
        $directoryId = $directoryIdByRelativePath[$childRelativePath]
        $escapedDirectoryName = Escape-Xml $directoryName

        $lines.Add("${baseIndent}<Directory Id=""$directoryId"" Name=""$escapedDirectoryName"">")
        Write-DirectoryNodes -ParentRelativePath $childRelativePath -Depth ($Depth + 1)
        $lines.Add("${baseIndent}</Directory>")
    }
}

Write-DirectoryNodes -ParentRelativePath "" -Depth 3

$lines.Add("${indent}${indent}</DirectoryRef>")
$lines.Add("${indent}</Fragment>")
$lines.Add("${indent}<Fragment>")
$lines.Add("${indent}${indent}<ComponentGroup Id=""PublishedComponents"">")
foreach ($componentId in ($componentEntries | Sort-Object -Unique)) {
    $lines.Add("${indent}${indent}${indent}<ComponentRef Id=""$componentId"" />")
}
$lines.Add("${indent}${indent}</ComponentGroup>")
$lines.Add("${indent}</Fragment>")
$lines.Add('</Wix>')

$outputDirectory = Split-Path -Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
}

[System.IO.File]::WriteAllLines($OutputPath, $lines)
Write-Host "Generated WiX fragment: $OutputPath"
