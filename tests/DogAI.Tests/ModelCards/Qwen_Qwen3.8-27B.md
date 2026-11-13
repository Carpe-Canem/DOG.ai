---
library_name: transformers
license: apache-2.0
pipeline_tag: image-text-to-text
---

# Qwen3.8-27B

> [!Note]
> This repository contains model weights and configuration files for the post-trained model in the Hugging Face Transformers format. 
>
> These artifacts are compatible with Hugging Face Transformers, vLLM, SGLang, TokenSpeed, etc.

> [!Tip]
> For users seeking managed, scalable inference without infrastructure maintenance, the official Qwen API service is provided by [Qwen Cloud](https://www.qwencloud.com).
> In particular, **Qwen3.8-27B** will be available as a hosted version with more production features, e.g., 1M context length by default, official built-in tools. For more information, please refer to the [Qwen3.8-27B Overview](https://www.qwencloud.com/models/qwen3.8-27b). The service is coming soon. Stay tuned for updates.

Following the widespread community adoption of the Qwen3.5 and Qwen3.6 series, we are pleased to introduce Qwen3.8, the most capable generation in the Qwen open-model family to date.

Built on the architectural foundation of Qwen3.5, Qwen3.8 delivers substantial gains across coding, professional work, research, and long-horizon agentic tasks. Qwen3.8-27B brings these advances to a compact, deployment-friendly dense model: a native vision-language model that understands images and videos, with flexible thinking control, designed to carry complex, multi-step tasks through to completion with greater reliability.

## Qwen3.8 Highlights

Qwen3.8-27B features the following enhancements:
- **Core Capabilities**: Comprehensive improvements across coding, professional work, research, and long-horizon agentic tasks.
- **Agent Execution**: Stronger autonomous planning and better handling of environment feedback, leading to more reliable end-to-end task completion.
- **Downstream Compatibility**: Broader support for popular harnesses and development tools, making it easier to integrate into your existing stack.
- **Flexible Thinking Control**: Thinking mode is on by default and can be disabled per request; reasoning depth can be tuned with `reasoning_effort`, and reasoning context from historical messages is retained via `preserve_thinking`.
- **Vision-Language Understanding**: Native support for image and video understanding, from STEM diagrams and documents to hour-scale videos.


## Model Overview

- Type: Causal Language Model with Vision Encoder
- Training Stage: Pre-training & Post-training
- Language Model
    - Number of Parameters: 27B
    - Hidden Dimension: 5120
    - Token Embedding: 248,320 (Padded)
    - Number of Layers: 64
    - Hidden Layout: 16 × (3 × (Gated DeltaNet → FFN) → 1 × (Gated Attention → FFN))
    - Gated DeltaNet:
        - Number of Linear Attention Heads: 48 for V and 16 for QK
        - Head Dimension: 128
    - Gated Attention:
        - Number of Attention Heads: 24 for Q and 4 for KV
        - Head Dimension: 256
        - Rotary Position Embedding Dimension: 64
    - Feed Forward Network:
        - Intermediate Dimension: 17,408
    - LM Output: 248,320 (Padded)
    - MTP (Multi-Token Prediction): trained with multiple steps
- Context Length: 262,144 natively and extensible up to 1,000,000 tokens.


## Benchmark Results

### Text Performance
<style>
.vl-table th{font-size:15px!important;line-height:1.2;color:inherit!important}
.vl-table td:not(.benchmark-cell):not([colspan]){font-size:15px;line-height:1.2;vertical-align:middle;color:inherit!important}
.vl-table .benchmark-cell{padding:12px 10px 12px 18px!important;vertical-align:middle}
.vl-table .benchmark-capability{font-size:15px;font-weight:600;line-height:1.22;color:inherit!important}
.vl-table .benchmark-name{margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;opacity:.6}
.vl-table .metric-stack{display:flex;flex-direction:column;gap:7px;padding:3px 0}
.vl-table .metric-label{font-size:10px;font-weight:400;line-height:1.1;opacity:.55}
.vl-table .metric-value{margin-top:2px;font-size:15px;line-height:1.15;color:inherit!important}
</style>
<div style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:1200px;margin:0 auto;padding:16px 0">
<table class="vl-table" style="width:100%;table-layout:fixed;border-collapse:collapse;font-size:13px">
<thead><tr>
<th style="padding:10px 7px;text-align:left;font-weight:600;border-bottom:2px solid #0A2EFE;color:#0A2EFE"></th><th style="padding:10px 7px;text-align:center;font-weight:500;border-bottom:2px solid #0A2EFE;color:#0A2EFE;font-size: 14px;width:14.00%;background:rgba(10, 46, 254, 0.08);">Qwen3.8-27B</th><th style="padding:10px 7px;text-align:center;font-weight:500;border-bottom:2px solid #0A2EFE;color:#0A2EFE;font-size: 14px;width:14.00%;">Qwen3.6-27B</th><th style="padding:10px 7px;text-align:center;font-weight:500;border-bottom:2px solid #0A2EFE;color:#0A2EFE;font-size: 14px;width:14.00%;">Qwen3.7-Plus</th><th style="padding:10px 7px;text-align:center;font-weight:500;border-bottom:2px solid #0A2EFE;color:#0A2EFE;font-size: 14px;width:14.00%;">Muse Glimmer-30B</th><th style="padding:10px 7px;text-align:center;font-weight:500;border-bottom:2px solid #0A2EFE;color:#0A2EFE;font-size: 14px;width:14.00%;">Opus4.6 Max</th></tr></thead>
<tbody>
<tr><td colspan="6" style="padding:8px 12px;font-weight:600;color:#0A2EFE;border-bottom:1px solid rgba(10, 46, 254, 0.2);background:#D6DAFC">Coding</td></tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Agentic terminal coding</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">Terminal Bench 2.1 (Terminus)</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;">73.0</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">63.4</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">64.0</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">51.7</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>78.2</strong></td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Agentic coding</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">SWE-bench Pro</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>61.7</strong></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">53.5</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">57.6</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">51.2</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">53.4</td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Repo-level code generation</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">NL2Repo-Bench</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;">42.3</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">36.2</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">41.1</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>47.6</strong></td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Agentic coding</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">DeepSWE 1.1</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>42.2</strong></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">13.3</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">14.2</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Software engineering</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">QwenSWEBench</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>79.0</strong></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">49.3</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">59.2</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">63.8</td>
</tr>
<tr><td colspan="6" style="padding:8px 12px;font-weight:600;color:#0A2EFE;border-bottom:1px solid rgba(10, 46, 254, 0.2);background:#D6DAFC">Agent</td></tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Long-horizon office work</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">CoWorkBench</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>70.7</strong></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">61.0</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">65.1</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">68.2</td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Professional job tasks</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">JobBench</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><strong>33.4</strong></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">21.8</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">27.6</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
</tr>
<tr>
<td class="benchmark-cell" style="padding:7px 7px;padding-left:20px;border-bottom:1px solid rgba(128, 128, 128, 0.15);"><div class="benchmark-capability" style="font-size:15px;font-weight:600;line-height:1.22;color:#171717">Frontier agentic tasks</div><div class="benchmark-name" style="margin-top:4px;font-size:11px;font-weight:400;line-height:1.2;color:#6B6B6B">Agents' Last Exam</div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);background:rgba(10, 46, 254, 0.08);vertical-align:middle;font-size:15px;line-height:1.2;"><div class="metric-stack" style="padding:3px 0"><div><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Pass@1</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717"><strong>20.4</strong></div></div><div style="margin-top:7px"><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Score</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717"><strong>42.9</strong></div></div></div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;"><div class="metric-stack" style="padding:3px 0"><div><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Pass@1</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717">10.6</div></div><div style="margin-top:7px"><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Score</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717">27.3</div></div></div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;"><div class="metric-stack" style="padding:3px 0"><div><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Pass@1</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717">13.2</div></div><div style="margin-top:7px"><div class="metric-label" style="font-size:10px;font-weight:400;line-height:1.1;color:#777">Score</div><div class="metric-value" style="margin-top:2px;font-size:15px;line-height:1.15;color:#171717">33.6</div></div></div></td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
<td style="padding:7px 7px;text-align:center;border-bottom:1px solid rgba(128, 128, 128, 0.15);vertical-align:middle;font-size:15px;line-height:1.2;">--</td>
</tr>
<tr><td colspan="6" style="padding:8px 12px;font-weight:600;color:#0A2EFE;border-bottom:1px solid rgba(10, 46, 254, 0.2);background:#D6DAFC">General</td></tr>
