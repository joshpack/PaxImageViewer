# Builds the standalone viewer as one native exe (no .NET install needed) into .\dist.
# The native linker can't work from a network-mapped drive, so the build runs from a copy on the local disk.
param(
    [string]$Out = "$PSScriptRoot\dist",
    [string]$Work = "C:\dev\PaxImageViewer-build"
)
$ErrorActionPreference = 'Stop'

# The AOT toolchain finds the C++ linker with vswhere, which isn't on PATH by default.
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$installer\vswhere.exe") -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }

robocopy $PSScriptRoot $Work /MIR /XD bin obj dist .git .vs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the source failed ($LASTEXITCODE)." }

dotnet publish "$Work\App\PaxImageViewer.csproj" -c Release -r win-x64 -o "$Work\publish" --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem "$Work\publish" -Filter *.pdb | Remove-Item
Copy-Item "$Work\publish\*" $Out -Recurse -Force
Get-ChildItem $Out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
