param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$sources = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs'),
    (Join-Path $PSScriptRoot 'ShaderCache.cs'), (Join-Path $PSScriptRoot 'ShaderSync.cs')).FullName
$icon = Join-Path $PSScriptRoot 'assets\jinra.ico'

& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Net.Http.dll `
    /reference:System.Runtime.Serialization.dll "/win32icon:$icon" "/out:$output\Jinra Shader Sync.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Jinra Shader Sync did not compile.' }

