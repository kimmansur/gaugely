<#
.SYNOPSIS
    Generates src/RateTray/app.ico.

.DESCRIPTION
    The icon is the Gaugely mark — a G that is also a gauge, with the needle in its hub — drawn
    in code by src/RateTray/Ui/GaugelyMark.cs. That class is the single source of the mark: the
    app icon, the settings window's brand and the tray's neutral icon all come from it. This
    script only asks a Release build to write the .ico (`Gaugely.exe --render-icon <path>`).

    Build, run this, then build again so the new icon is embedded.

.EXAMPLE
    dotnet build src/RateTray -c Release
    powershell -File tools/New-AppIcon.ps1
    dotnet build src/RateTray -c Release
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\RateTray\app.ico')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot "..\src\RateTray\bin\$Configuration\net9.0-windows\Gaugely.exe"
if (-not (Test-Path $exe)) { throw "Build first: $exe not found." }

$process = Start-Process -FilePath $exe -ArgumentList @('--render-icon', (Resolve-Path -LiteralPath (Split-Path $OutputPath)).Path + '\' + (Split-Path $OutputPath -Leaf)) -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) { throw "--render-icon failed (exit $($process.ExitCode))" }
"Wrote $OutputPath"
