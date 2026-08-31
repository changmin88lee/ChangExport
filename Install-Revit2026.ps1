param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assemblyCandidates = @(
    (Join-Path $root "ChangExport.dll"),
    (Join-Path $root "bin\$Configuration\net8.0-windows\ChangExport.dll")
) | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Sort-Object LastWriteTimeUtc -Descending

if ($assemblyCandidates.Count -eq 0) {
    throw "ChangExport.dll was not found. Run Build-Latest.cmd first."
}

$sourceAssembly = $assemblyCandidates[0].FullName
$addinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026"
$installDirectory = Join-Path $addinsDirectory "ChangExport"
$installedAssembly = Join-Path $installDirectory "ChangExport.dll"
$manifestPath = Join-Path $addinsDirectory "ChangExport.addin"

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceAssembly -Destination $installedAssembly -Force

$sourceNotices = Join-Path $root "ThirdParty"
if (Test-Path -LiteralPath $sourceNotices) {
    $installedNotices = Join-Path $installDirectory "ThirdParty"
    New-Item -ItemType Directory -Path $installedNotices -Force | Out-Null
    foreach ($notice in @("ACadSharp-LICENSE.txt", "CSUtilities-LICENSE.txt")) {
        Copy-Item -LiteralPath (Join-Path $sourceNotices $notice) -Destination (Join-Path $installedNotices $notice) -Force
    }
}

$sourceData = Join-Path $root "Data"
if (Test-Path -LiteralPath $sourceData) {
    $installedData = Join-Path $installDirectory "Data"
    New-Item -ItemType Directory -Path $installedData -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceData "Company_Default.json") -Destination (Join-Path $installedData "Company_Default.json") -Force
}

$escapedAssembly = [System.Security.SecurityElement]::Escape($installedAssembly)
$manifest = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>ChangExport</Name>
    <Assembly>$escapedAssembly</Assembly>
    <AddInId>AED168FA-7C43-497A-8F2D-F87905A2B2B3</AddInId>
    <FullClassName>ChangExport.App.App</FullClassName>
    <VendorId>LAON</VendorId>
    <VendorDescription>ChangExport tools for Autodesk Revit</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8

$versionPath = Join-Path $root "VERSION"
$version = if (Test-Path -LiteralPath $versionPath) {
    (Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8).Trim()
} else {
    "Beta"
}

Write-Host "ChangExport $version installation complete"
Write-Host "DLL:   $installedAssembly"
Write-Host "ADDIN: $manifestPath"
Write-Host "Close Revit 2026 completely, then start it again."
