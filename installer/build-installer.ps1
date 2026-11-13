$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root 'SYSMR.UI\SYSMR.UI.csproj') -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale em https://jrsoftware.org/isdl.php' }

& $iscc (Join-Path $PSScriptRoot 'SYSMR.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador.' }

Write-Host "Instalador gerado em $root\artifacts\installer"
