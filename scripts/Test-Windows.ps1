[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testProject = Join-Path $projectRoot 'tests\Winference.Tests\Winference.Tests.csproj'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$arguments = @('test', $testProject, '-c', 'Debug', '--nologo', '--logger', 'console;verbosity=normal')
if ($Filter) { $arguments += @('--filter', $Filter) }
& $DotnetPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
