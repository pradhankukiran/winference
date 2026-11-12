# Embeddable Lemonade

Version: 2026.46.2. Upstream release: https://github.com/lemonade-sdk/lemonade/releases/tag/v2026.46.2

Official Windows x64 ZIP (lemonade-embeddable-2026.46.2-windows-x64.zip) SHA-256: 8037d7a2d79fb7898802d7f4e4c7f605b91951328ee29d82d893e29ba1c87b00. This directory contains the unmodified `lemond.exe`, its resources and LICENSE from that archive. The unused CLI (`lemonade.exe`) is excluded. No inference engine or model weights are bundled. FastFlowLM is discovered separately; Winference pins FastFlowLM 1.0.12, newer than the 1.0.11 this Lemonade release lists, and runs it through the same pass-through `flm serve` route.
