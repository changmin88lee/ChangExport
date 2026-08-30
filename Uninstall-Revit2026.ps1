$ErrorActionPreference = "Stop"

$addinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2026"
$installDirectory = Join-Path $addinsDirectory "ChangExport"
$manifestPath = Join-Path $addinsDirectory "ChangExport.addin"

if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}

if (Test-Path -LiteralPath $installDirectory) {
    $resolvedInstall = (Resolve-Path -LiteralPath $installDirectory).Path
    $resolvedAddins = (Resolve-Path -LiteralPath $addinsDirectory).Path
    if (-not $resolvedInstall.StartsWith($resolvedAddins, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe uninstall path: $resolvedInstall"
    }
    Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
}

Write-Host "ChangExport for Revit 2026 was removed."
Write-Host "The user profile under %APPDATA%\ChangExport was preserved."
