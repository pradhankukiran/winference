# Chat template test data

Copied unmodified from llama.cpp v1.0.0 (commit b1b770d90d94cad9e9ea5b993bdbc383a522fb28), MIT License, Copyright (c) 2023-2026 The ggml authors.

- `templates/*.jinja`: real model chat templates from `models/templates`. Each one belongs to its model's publisher; llama.cpp keeps them for testing.
- `llama-cpp-chat-templates.json`: the template cases and expected outputs from `tests/test-chat-template.cpp`, converted to JSON. The conversation they render is in `ChatTemplateTests`.
