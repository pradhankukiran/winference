# ODS source used by Winference

Source repository: https://github.com/Osmantic/ODS

Source revision: 72e59cdc665fc576d300061cabb5b980cbbaf1da. LICENSE is the upstream Apache-2.0 text. File SHA256 values are recorded in SOURCE-MANIFEST.json.

Winference keeps two pieces derived from ODS:

- The bounded GGUF metadata reader, src/Winference.Infrastructure/Gpu/GgufMetadataReader.cs, is a native C# port of ods/extensions/services/dashboard-api/gguf_inspector.py. It does not ship Python or call the ODS dashboard API.
- Selected GGUF entries in config/catalogs/gpu-approved-1.json are adapted from ods/config/model-library.json. Source URLs, file sizes and SHA-256 values are copied unchanged.

Earlier builds also shipped the ODS PowerShell launcher helpers (native-llama-runtime.ps1 and private-file.ps1) to start llama-server. They were removed when the GPU mode moved to Winference's own engine worker, which calls llama.cpp directly.
