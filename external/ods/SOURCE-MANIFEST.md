# ODS source extraction for Winference GPU mode

Source repository: https://github.com/Osmantic/ODS

Source revision: 72e59cdc665fc576d300061cabb5b980cbbaf1da. The source checkout remains read-only. File SHA256 values are recorded in SOURCE-MANIFEST.json.

The two standalone files under windows/ are byte-for-byte copies of ods/installers/windows/lib/native-llama-runtime.ps1 and private-file.ps1. Their original headers are retained. LICENSE is the upstream Apache-2.0 text. The complete standalone module is retained to preserve its local dependency closure; Winference invokes only its native Vulkan argument, qualification, private-file and readiness functions. CUDA routes, installers, startup tasks, environment migration and acquisition functions are not invoked by the bridge. No Pixel source or branding is included.

The bounded GGUF metadata reader is a native C# port attributed to ods/extensions/services/dashboard-api/gguf_inspector.py. It does not ship Python or call the ODS dashboard API. Its exact source hash and destination are recorded separately in the JSON manifest.

## Retained source used by the bridge

- Test-ODSNativeLlamaInstall rechecks the exact release/archive and every runtime payload file. Pinning is anchored to the retained, SHA256-verified upstream archive by Winference's separate acquisition service.
- Test-ODSNativeLlamaQualification checks the native version/build and parses --list-devices on a future explicit load. No qualification command has been executed during this implementation.
- Select-ODSNativeLlamaDevice and ConvertTo-ODSNativeLlamaAdapterKey supply upstream Vulkan selection/name normalization. Winference additionally requires a unique match to a PCI VEN_1002 Windows adapter; the permissive single-AMD fallback does not establish readiness.
- Assert-ODSNativeLlamaPlan, Assert-ODSNativeLlamaOptions and New-ODSNativeLlamaLaunchArguments retain the filename/path/argument restrictions, ASCII/8.3 conversion, loopback host, one slot, exact model alias, key-file authentication, GPU layer count and Vulkan device.
- The API-key and private-file helpers create the key and bridge evidence with a current-user-only DACL. The key is never placed in command-line arguments.
- Wait-ODSNativeLlamaStartup, Assert-ODSNativeLlamaListener and Get-ODSNativeLlamaModelProof retain /health, /v1/models, /props, model-path, one-slot and 256-cell context-alignment checks.

## Winference-owned adaptation

src/Winference.Infrastructure/Ods/Invoke-GpuRuntime.ps1 is a new application bridge. It takes one private versioned JSON request, rejects CPU and ambiguous-device routes, hashes the selected external GGUF, performs necessary available-memory checks, requires reported full GPU layer placement, writes a bounded private readiness record and waits for its own child. These are implementation requirements, not measured GPU results.

The bridge deliberately uses ProcessStartInfo.UseShellExecute=false instead of calling the upstream durable Start-Process launcher. The existing Windows job starts the bridge suspended, assigns it before resume, and is intended to contain its direct native child and qualification probes. The C# adapter rechecks actual job membership, process start time/image, owned loopback listener, model/alias/context and offload evidence before publishing Ready. It never adopts a separately running ODS or llama-server instance.

The backend retains a read handle on the model throughout verification and execution, preventing write/replacement races. Cancellation ends only its owned job and waits for its children before releasing that handle. It uses the existing bounded completion-stream parser with pinned llama.cpp HTTP endpoints and completion-reason checks. Metadata/prompt rendering and token checks are delegated to the selected runtime; no byte-to-token estimate is claimed.

The memory checks are conservative prerequisites, not a measured Vulkan fit envelope. The original ODS memory guard covers CUDA only and is not represented as AMD Vulkan qualification. The exact AMD device mapping, layer-log format, API/template/token counts, allocation behavior, cancellation and cleanup still require the user's later GPU test.

## Execution status

Implemented only. At the user's request, no tests, PowerShell helper loading, native --version/--list-devices, model load, GPU inference or application launch were performed for this phase. No model or runtime binary was downloaded by this extraction. A compile-only build by the main agent is recorded separately and must not be described as a GPU test.

The selected runtime is llama.cpp b12109, native Windows Vulkan x64 (originally b9014). Its layer-placement log line needs verbosity 4, which the bridge sets through LLAMA_ARG_LOG_VERBOSITY. Runtime binary and DLL license/notices remain those of the complete upstream distribution; Apache-2.0 applies to the extracted ODS source, not automatically to its native dependencies. The runtime acquisition preserves the complete payload and its notices.
