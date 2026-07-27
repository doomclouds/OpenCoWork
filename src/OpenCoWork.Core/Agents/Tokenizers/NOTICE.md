# Tokenizer assets

The compressed files in this directory are redistributed only as tokenizer
data. OpenCoWork validates the SHA-256 of the decompressed `tokenizer.json`
before loading it and never downloads tokenizer assets at runtime.

| Asset | Upstream revision | Decompressed SHA-256 | License |
| --- | --- | --- | --- |
| `deepseek-v4.tokenizer.json.gz` | `deepseek-ai/DeepSeek-V4-Pro@b5968e9190ef611bbf34a7229255be88a0e937c1`; identical to `DeepSeek-V4-Flash@60d8d70770c6776ff598c94bb586a859a38244f1` | `8f9f37ca37fdc4f5fd36d5cf4d3b0e8392edb4e894fd10cc0d70b4957c8633cf` | MIT |
| `glm-5.2.tokenizer.json.gz` | `zai-org/GLM-5.2@b4734de4facf877f85769a911abafc5283eab3d9` | `19e773648cb4e65de8660ea6365e10acca112d42a854923df93db4a6f333a82d` | MIT |

`qwen3.8-max-preview` uses Tiktoken's built-in `o200k_base`, following the
QwenCloud token-counting guidance; it has no separate asset in this directory.
