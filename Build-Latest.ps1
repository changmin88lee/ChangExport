param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "ChangExport.csproj"
$target = Join-Path $root "bin\$Configuration\net8.0-windows\ChangExport.dll"
$deployFolderName = ([char]0xBC30) + ([char]0xD3EC) + ([char]0xD3F4) + ([char]0xB354)
$latestFolderName = ([char]0xCD5C) + ([char]0xC2E0)
$installFileName = ([char]0xC124) + ([char]0xCE58) + ".cmd"
$uninstallFileName = ([char]0xC81C) + ([char]0xAC70) + ".cmd"
$buildInfoFileName = ([char]0xBE4C) + ([char]0xB4DC) + ([char]0xC815) + ([char]0xBCF4) + ".txt"
$latest = Join-Path (Join-Path $root $deployFolderName) $latestFolderName

if (-not $SkipBuild) {
    dotnet build $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "ChangExport build failed."
    }
}

if (-not (Test-Path -LiteralPath $target)) {
    throw "Release DLL was not found: $target"
}

New-Item -ItemType Directory -Path $latest -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $latest "Data") -Force | Out-Null

Copy-Item -LiteralPath $target -Destination (Join-Path $latest "ChangExport.dll") -Force
Copy-Item -LiteralPath (Join-Path $root "Install.cmd") -Destination (Join-Path $latest $installFileName) -Force
Copy-Item -LiteralPath (Join-Path $root "Install-Revit2026.ps1") -Destination (Join-Path $latest "Install-Revit2026.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "Uninstall.cmd") -Destination (Join-Path $latest $uninstallFileName) -Force
Copy-Item -LiteralPath (Join-Path $root "Uninstall-Revit2026.ps1") -Destination (Join-Path $latest "Uninstall-Revit2026.ps1") -Force
Copy-Item -LiteralPath (Join-Path $root "VERSION") -Destination (Join-Path $latest "VERSION") -Force
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $latest "README.md") -Force
Copy-Item -LiteralPath (Join-Path $root "Data\Company_Default.json") -Destination (Join-Path $latest "Data\Company_Default.json") -Force
New-Item -ItemType Directory -Path (Join-Path $latest "ThirdParty") -Force | Out-Null
foreach ($notice in @("ACadSharp-LICENSE.txt", "CSUtilities-LICENSE.txt")) {
    Copy-Item -LiteralPath (Join-Path $root "ThirdParty\$notice") -Destination (Join-Path $latest "ThirdParty\$notice") -Force
}

$buildInfo = @"
ChangExport latest Release build
Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Target: Autodesk Revit 2026 / Windows x64 / .NET 8
DWG engine: Embedded ACadSharp 3.7.1 (MIT). No external CAD installation or process.
Version: $((Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw -Encoding UTF8).Trim())
"@
Set-Content -LiteralPath (Join-Path $latest $buildInfoFileName) -Value $buildInfo -Encoding UTF8

Write-Host "Latest build updated: $latest"
