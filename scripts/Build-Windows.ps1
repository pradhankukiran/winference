[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish,
    # Publishes, then builds the Inno Setup installer (implies -Publish).
    [switch]$Installer,
    [string]$InnoCompilerPath = '',
    # Overrides <Version> from the app project, for example 1.0.0.42 for a CI build.
    [string]$Version = '',
    [switch]$UpdateLockFile,
    [string]$OutputDirectory = 'artifacts\win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $projectRoot 'src\Winference.App\Winference.App.csproj'
$dotnetCommand = Get-Command $DotnetPath -ErrorAction Stop
$dotnetExecutable = $dotnetCommand.Source
if (-not $dotnetExecutable) { $dotnetExecutable = $DotnetPath }

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

if ($Installer) { $Publish = $true }
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version '$Version' must look like 1.2.3 or 1.2.3.4."
}

Push-Location $projectRoot
try {
    $restoreArguments = @('restore', $projectPath, '-r', 'win-x64', '-p:Platform=x64')
    if (-not $UpdateLockFile) { $restoreArguments += '--locked-mode' }
    & $dotnetExecutable @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

    if ($Publish) {
        $destination = Join-Path $projectRoot $OutputDirectory
        $publishArguments = @('publish', $projectPath, '-c', $Configuration, '-r', 'win-x64',
            '-p:Platform=x64', '--self-contained', 'true', '--no-restore', '-o', $destination)
        if ($Version) { $publishArguments += "-p:Version=$Version" }
        & $dotnetExecutable @publishArguments
        if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
        if (-not (Test-Path (Join-Path $destination 'Winference.pri'))) {
            throw 'Windows publish is missing the compiled application resources (Winference.pri).'
        }
        Write-Output "WINDOWS_APP=$destination\Winference.exe"

        if ($Installer) {
            if (-not $InnoCompilerPath) {
                $InnoCompilerPath = @(
                    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
                    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
                ) | Where-Object { Test-Path $_ } | Select-Object -First 1
            }
            if (-not $InnoCompilerPath -or -not (Test-Path $InnoCompilerPath)) {
                throw 'Inno Setup 6 (ISCC.exe) was not found. Install it or pass -InnoCompilerPath.'
            }
            $version = $Version
            if (-not $version) {
                [xml]$project = Get-Content $projectPath
                $version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
            }
            if (-not $version) { throw 'The app project has no <Version>.' }
            $installerOutput = Join-Path $projectRoot 'artifacts\installer'
            & $InnoCompilerPath '/Q' "/DAppVersion=$version" "/DSourceDir=$destination" "/DOutputDir=$installerOutput" `
                (Join-Path $projectRoot 'installer\Winference.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
            Write-Output "WINDOWS_INSTALLER=$installerOutput\Winference-Setup-$version.exe"
        }
    }
    else {
        & $dotnetExecutable build $projectPath '-c' $Configuration '-p:Platform=x64' '--no-restore'
        if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
    }
}
finally {
    Pop-Location
}
