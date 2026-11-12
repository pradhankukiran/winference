[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RequestPath)

# Winference-owned bridge around the attributed ODS standalone helpers.
# The Windows app starts this supervisor suspended inside its kill-on-close job.
# No installer, scheduled task, legacy environment or CPU fallback is involved.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'native-llama-runtime.ps1')

$request = $null
$child = $null
$stage = 'request'
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
    Protect-ODSPrivateEnvFile $RequestPath
    $request = Read-ODSNativeLlamaJson $RequestPath
    $sessionDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($RequestPath))
    if ($request.schemaVersion -ne 1 -or [string]$request.sessionId -cnotmatch '^[0-9a-f]{32}$' -or
        [string]$request.releaseTag -cne 'b12109' -or
        [string]$request.archiveSha256 -cne 'ed1d85207f58a292b5e9b9b529ecd4439bb8a60c5460dab0c137ef94d4322f2a' -or
        [string]$request.modelSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [long]$request.modelLengthBytes -le 0 -or [int]$request.contextSize -lt 256 -or [int]$request.contextSize -gt 131072 -or
        [int]$request.port -lt 1 -or [int]$request.port -gt 65535) { throw 'The GPU launch request is invalid.' }
    foreach ($field in @('executablePath', 'modelPath')) {
        if (-not [IO.Path]::IsPathRooted([string]$request.$field)) { throw 'The GPU launch paths must be absolute.' }
    }
    if ([IO.Path]::GetFileName([string]$request.executablePath) -ine 'llama-server.exe') { throw 'The GPU executable is invalid.' }
    $readyPath = Join-Path $sessionDirectory 'ready.json'
    $failurePath = Join-Path $sessionDirectory 'failure.json'
    $keyPath = Join-Path $sessionDirectory 'api.key'
    $logPath = Join-Path $sessionDirectory 'runtime.log'

    $stage = 'runtime'
    $null = Test-ODSNativeLlamaInstall -Directory (Split-Path -Parent ([string]$request.executablePath)) `
        -ExpectedZipSha256 ([string]$request.archiveSha256) -ExpectedReleaseTag ([string]$request.releaseTag)
    $model = Get-Item -LiteralPath ([string]$request.modelPath) -ErrorAction Stop
    if ($model.PSIsContainer -or $model.Length -ne [long]$request.modelLengthBytes -or
        (Get-FileHash -LiteralPath $model.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$request.modelSha256) {
        throw 'The selected GGUF changed after it was registered. Select it again before loading.'
    }

    $stage = 'device'
    $qualification = Test-ODSNativeLlamaQualification -ExecutablePath ([string]$request.executablePath) -ExpectedBuild 12109 -Backend 'vulkan' -Architecture 'x64'
    $adapters = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Where-Object {
        [string]$_.PNPDeviceID -match '^PCI\\VEN_1002&' -and [string]$_.Name -match '(?i)radeon|amd'
    })
    # $Matches is PowerShell's automatic regex result; keep this collection separate.
    $adapterMatches = @()
    foreach ($adapter in $adapters) {
        $device = Select-ODSNativeLlamaDevice -Devices @($qualification.Devices) -AdapterName ([string]$adapter.Name) -Backend 'vulkan'
        if ($null -eq $device) { continue }
        $physical = ConvertTo-ODSNativeLlamaAdapterKey ([string]$adapter.Name)
        $reported = ConvertTo-ODSNativeLlamaAdapterKey ([string]$device.Description)
        $matchingDevices = @($qualification.Devices | Where-Object {
            $name = ConvertTo-ODSNativeLlamaAdapterKey ([string]$_.Description)
            [string]$_.Name -cmatch '^Vulkan[0-9]{1,2}$' -and $physical -and $name -and
                ($physical -ceq $name -or $physical.Contains($name) -or $name.Contains($physical))
        })
        # Do not use the upstream single-AMD fallback when names cannot establish a physical mapping.
        if ($matchingDevices.Count -eq 1 -and $physical -and $reported -and
            ($physical -ceq $reported -or $physical.Contains($reported) -or $reported.Contains($physical))) {
            $adapterMatches += [pscustomobject]@{ Adapter = $adapter; Device = $device }
        }
    }
    if ($adapterMatches.Count -ne 1) { throw 'A unique AMD Radeon Vulkan device could not be matched. Check the GPU driver; CPU fallback was not used.' }
    $selected = $adapterMatches[0]
    if ([string]$selected.Device.Name -cnotmatch '^Vulkan[0-9]{1,2}$') { throw 'The selected device is not a Vulkan GPU.' }

    $stage = 'memory'
    $operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $availableBytes = [long]$operatingSystem.FreePhysicalMemory * 1024L
    # Necessary headroom checks, not a measured total-memory-fit guarantee.
    if ($availableBytes -lt [long]$request.modelLengthBytes + 1GB -or
        [long]$selected.Device.FreeMiB * 1MB -lt [long]$request.modelLengthBytes) {
        throw 'Available system or Vulkan memory is below the model-file size plus required headroom. Free memory or choose a smaller GGUF.'
    }

    $apiKey = New-ODSNativeLlamaApiKey
    Write-ODSNativeLlamaApiKeyFile -Path $keyPath -Key $apiKey
    Write-ODSPrivateEnvFile -Path $logPath -Content ''
    $plan = [pscustomobject]@{
        ExecutablePath = [string]$request.executablePath
        ModelsDir = $model.DirectoryName
        GgufFile = $model.Name
        ContextSize = [int]$request.contextSize
        Port = [int]$request.port
        MmprojFile = $null
    }
    $options = [pscustomobject]@{
        schemaVersion = 1; Backend = 'vulkan'; Architecture = 'x64'
        Device = [string]$selected.Device.Name; NGpuLayers = 'all'
        ApiKeyPath = $keyPath; LogPath = $logPath
        ReleaseTag = [string]$request.releaseTag; ZipSha256 = [string]$request.archiveSha256
        ReasoningArguments = @(); ExtraArguments = @()
    }
    Assert-ODSNativeLlamaPlan $plan
    Assert-ODSNativeLlamaOptions $options
    $arguments = @(New-ODSNativeLlamaLaunchArguments $plan $options)
    if (@(Get-NetTCPConnection -LocalPort $plan.Port -State Listen -ErrorAction SilentlyContinue).Count) {
        throw 'The selected local port is occupied. Existing processes were preserved; retry the load.'
    }
    $stage = 'startup'
    # UseShellExecute=false starts a direct child that inherits the app-owned job.
    # Do not invoke the upstream durable Start-Process launcher or adopt an existing server.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $plan.ExecutablePath
    $start.Arguments = ConvertTo-ODSNativeLlamaArgumentString $arguments
    $start.WorkingDirectory = Split-Path -Parent $plan.ExecutablePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    # Newer llama.cpp logs layer placement ("offloaded N/N layers to GPU") only at verbosity 4.
    $start.EnvironmentVariables['LLAMA_ARG_LOG_VERBOSITY'] = '4'
    $child = [Diagnostics.Process]::Start($start)
    $null = $child.Handle
    Wait-ODSNativeLlamaStartup -Port $plan.Port -Process $child -TimeoutSeconds 150 -Backend 'vulkan' -Architecture 'x64'
    Assert-ODSNativeLlamaListener $plan.Port $child.Id $plan.ExecutablePath
    $proof = Get-ODSNativeLlamaModelProof -Port $plan.Port -GgufFile $plan.GgufFile `
        -ModelPaths @($arguments[1], $model.FullName) -ContextSize $plan.ContextSize -ApiKey $apiKey
    if (-not $proof.ContextVerified) { throw $proof.Message }

    $stage = 'offload'
    # llama-server keeps its log open for writing. Read a bounded snapshot while
    # sharing that writer instead of File.ReadAllText's read-only sharing mode.
    $logStream = [IO.FileStream]::new($logPath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($logStream.Length -gt 8MB) { throw 'GPU startup diagnostics exceeded the supported size. Readiness was not claimed.' }
        $logBytes = [byte[]]::new([int]$logStream.Length)
        $bytesRead = 0
        while ($bytesRead -lt $logBytes.Length) {
            $count = $logStream.Read($logBytes, $bytesRead, $logBytes.Length - $bytesRead)
            if ($count -eq 0) { break }
            $bytesRead += $count
        }
        $log = [Text.Encoding]::UTF8.GetString($logBytes, 0, $bytesRead)
    }
    finally { $logStream.Dispose() }
    $offloads = [regex]::Matches($log, '(?im)\boffloaded\s+(?<loaded>[0-9]+)\s*/\s*(?<total>[0-9]+)\s+layers\s+to\s+GPU\b')
    if ($offloads.Count -eq 0) { throw 'The runtime did not report GPU layer placement. GPU readiness was not claimed.' }
    $offload = $offloads[$offloads.Count - 1]
    $loadedLayers = [int]$offload.Groups['loaded'].Value
    $totalLayers = [int]$offload.Groups['total'].Value
    if ($loadedLayers -le 0) { throw 'The runtime did not offload any model layers to the selected GPU. Check your GPU driver or available memory.' }
    Assert-ODSNativeLlamaListener $plan.Port $child.Id $plan.ExecutablePath
    $ready = [ordered]@{
        schemaVersion = 1; sessionId = [string]$request.sessionId; supervisorId = $PID
        processId = $child.Id; startedUtc = $child.StartTime.ToUniversalTime().ToString('o')
        executablePath = $plan.ExecutablePath; port = $plan.Port; modelId = $proof.ModelId
        modelPath = $model.FullName; modelArgumentPath = $arguments[1]
        modelSha256 = [string]$request.modelSha256; modelLengthBytes = [long]$request.modelLengthBytes
        contextSize = $plan.ContextSize; runtimeContext = [long]$proof.RuntimeContext
        releaseTag = [string]$request.releaseTag; build = [int]$qualification.Build; commit = [string]$qualification.Commit
        device = [string]$selected.Device.Name; deviceDescription = [string]$selected.Device.Description
        adapterName = [string]$selected.Adapter.Name; adapterPnpId = [string]$selected.Adapter.PNPDeviceID
        driverVersion = [string]$selected.Adapter.DriverVersion
        offloadedLayers = $loadedLayers; totalLayers = $totalLayers
    }
    Write-ODSPrivateEnvFile -Path $readyPath -Content ($ready | ConvertTo-Json -Compress -Depth 5)
    $child.WaitForExit()
    if ($child.ExitCode -ne 0) { throw 'The owned GPU runtime stopped unexpectedly. Retry loading the model.' }
}
catch {
    if ($null -ne $request) {
        try {
            $record = [ordered]@{ schemaVersion = 1; stage = $stage; message = [string]$_.Exception.Message }
            Write-ODSPrivateEnvFile -Path (Join-Path (Split-Path -Parent $RequestPath) 'failure.json') -Content ($record | ConvertTo-Json -Compress)
        } catch { }
    }
    [Console]::Error.WriteLine('Winference GPU startup failed at stage ' + $stage + '.')
    exit 1
}
finally {
    if ($null -ne $child) {
        # This is a retained handle created by this bridge, never an adopted PID.
        if (-not $child.HasExited) {
            $child.Kill()
            if (-not $child.WaitForExit(10000)) { throw 'The owned GPU process did not finish stopping.' }
        }
        $child.Dispose()
    }
}
