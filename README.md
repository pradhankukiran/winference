# Winference

Local AI chat for Windows on AMD Ryzen AI PCs. Winference runs models on your own
hardware: the NPU (FastFlowLM), the GPU (llama.cpp) or both together (Ryzen AI hybrid).
Nothing is sent to the cloud.

## Features

- Pick a model; the right engine is chosen for you. NPU, GPU and GPU + NPU are filters, not modes.
- Download, use and remove models from the Models page.
- Chat with custom instructions, response styles, edit and resend, regenerate and Markdown export.
- Attach documents, PDFs and pictures. Vision models can answer questions about pictures.
- Optional tools that ask before touching your files.
- Read aloud, speech input, image generation and search by meaning through Lemonade add-ons.
- Reply speed shown under each answer.

## Requirements

- Windows 11 22H2 or later, x64
- AMD Ryzen AI processor for NPU models; a supported GPU for GPU models
- .NET SDK 10.0.402 to build

## Build

```powershell
# Build
scripts\Build-Windows.ps1

# Run unit tests
scripts\Test-Windows.ps1

# Build the app folder and the installer (needs Inno Setup 6)
scripts\Build-Windows.ps1 -Installer
```

The installer is written to `artifacts\installer\Winference-Setup-<version>.exe`.
It installs per user, without admin rights.

## Project layout

| Folder | Contents |
|---|---|
| `src/Winference.Core` | Models, runtime contracts, chat and tool logic |
| `src/Winference.Infrastructure` | Engines (FastFlowLM, Lemonade, llama.cpp, Ryzen AI), downloads, storage |
| `src/Winference.App` | WinUI 3 desktop app |
| `tests/Winference.Tests` | Unit tests |
| `config` | Model catalogs and model profiles |
| `external` | Bundled Lemonade server and third-party notices |
| `installer` | Inno Setup script |

## Licenses

Third-party components and their licenses are listed under `external/notices` and `external/*/LICENSE`.
