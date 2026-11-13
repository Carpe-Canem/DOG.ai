---
library_name: transformers
license: apache-2.0
pipeline_tag: image-text-to-text
base_model:
- Qwen/Qwen3.8-27B
tags:
- abliterated
- uncensored
- huihui
- qwen3
- unsloth
- ternary
- 2-bit
- llama-cpp
- cuda
- metal
- on-device
- hybrid-attention
- prismml
- bonsai
---

# huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF


This is an uncensored version of [Qwen/Qwen3.8-27B](https://huggingface.co/Qwen/Qwen3.8-27B) created with abliteration (see [remove-refusals-with-transformers](https://github.com/Sumandora/remove-refusals-with-transformers) to know more about it).
This is a crude, proof-of-concept implementation to remove refusals from an LLM model without using TransformerLens.

## patch-unsloth 9

The newly added **Huihui-Qwen3.8-27B-abliterated-patch-all-5.gguf** contains **27** newly added weights and supports the quantized versions of **unsloth/Qwen3.8-27B-GGUF** updated on November 11, 2026.

The **patch-unsloth** here can patch all quantized variants of the latest version of [unsloth/Qwen3.8-27B-GGUF](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF), from **UD-IQ1_S** to **BF16**, which saves storage space; you need to run the command yourself to apply the patch. For details, 
Please refer to [README-patch-unsloth.md](patch-unsloth/README-patch-unsloth.md) in the [patch-unsloth](https://huggingface.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/tree/main/patch-unsloth) directory.


## Latest update 8

The newly added **Huihui-Qwen3.8-27B-abliterated-Underdog-Saluki** series come from [ConwayResearch/Underdog-Saluki-27B-1.0](https://huggingface.co/ConwayResearch/Underdog-Saluki-27B-1.0) and [ukisai/Swift-1.5-Qwen3.8-27B-GSQ-RCO-GGUF](https://huggingface.co/ukisai/Swift-1.5-Qwen3.8-27B-GSQ-RCO-GGUF). 
Only layers **22 to 52 (0-based indexing)** have been ablated, while the other layers remain unablated. It may come with a small disclaimer warning.

Due to ConwayResearch/Underdog-Saluki-27B-1.0 not providing an imatrix file, we will attempt to perform ablation by mixing the GGUF weights of Swift-1.5 and ConwayResearch/Underdog-Saluki-27B-1.0.

ConwayResearch/Underdog-Saluki-27B-1.0/Underdog-Saluki-27B-1.0-IQ2-mix.gguf only ablated 62 weights.

This is just a test/validation.

## Latest update 7

The newly added **Huihui-Qwen3.8-27B-abliterated-Swift** series come from [ukisai/Swift-1.5-Qwen3.8-27B-GSQ-RCO-GGUF](https://huggingface.co/ukisai/Swift-1.5-Qwen3.8-27B-GSQ-RCO-GGUF). 
Only layers **22 to 52 (0-based indexing)** have been ablated, while the other layers remain unablated. It may come with a small disclaimer warning.
This is just a test/validation.

## Latest update 6

The newly added **Huihui-Qwen3.8-27B-abliterated-Ternary** series come from [prism-ml/Ternary-Bonsai-2-27B-gguf](https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf
). 
Only layers **22 to 52 (0-based indexing)** have been ablated, while the other layers remain unablated. It may come with a small disclaimer warning.
The size after conversion may differ from the original GGUF (Some of the weights are converted from PTQ1 to Q2_K or Q3_K.).
This is just a test/validation.

The ternary hybrid-attention kernels live in the
[PrismML-Eng/llama.cpp](https://github.com/PrismML-Eng/llama.cpp) fork. **Stock llama.cpp will not run these files.**

## Latest update 5

The newly added **Huihui-Qwen3.8-27B-abliterated-GSQ-RCO** series come from [ISTA-DASLab/Qwen3.8-27B-GSQ-RCO-GGUF](https://huggingface.co/ISTA-DASLab/Qwen3.8-27B-GSQ-RCO-GGUF
). 
Only layers **22 to 52 (0-based indexing)** have been ablated, while the other layers remain unablated. It may come with a small disclaimer warning.
The size after conversion may differ from the original GGUF.

## Latest update 4

The newly added **Huihui-Qwen3.8-27B-abliterated-UD-DW** series come from [unsloth/Qwen3.8-27B-GGUF](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF). 
Only layers **22 to 52 (0-based indexing)** have been ablated, while the other layers remain unablated. It may come with a small disclaimer warning.
The size after conversion may differ from the original GGUF.

## Latest update

The newly added **Huihui-Qwen3.8-27B-abliterated-UD** series come from [unsloth/Qwen3.8-27B-GGUF](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF). 
Only layers **17 to 52 (0-based indexing)** have been ablated(Previously, The first 15 layers were retained without ablation), while the other layers remain unablated. 
The size after conversion may differ from the original GGUF.

Huihui-Qwen3.8-27B-abliterated-bf16.gguf has also been updated.

This helps retain more of the original model’s performance. MTP and visual has not been modified.

## Note
The first 15 layers were retained without ablation. MTP and visual has not been modified.

We have already converted the weights (token_embd,output,ffn_down,ssm_out,attn_output) that need to be ablated in the versions below Q8_0 from Q2_K, Q3_K, Q4_K, Q5_K, and Q6_K to Q8_0 to improve response quality, and changed the filename to K_L.

In the Q8_0 quantized version, we changed the Q8_0 weights (token_embd,output,ffn_down,ssm_out,attn_output) targeted for ablation to BF16 and renamed the file to Q8_0_L.

This is not a standard quantization, so you might find that Q2_K_L is larger than Q3_K and Q4_K.

## Specific Quantification Method 
Some people may misunderstand. The specific quantification method is as follows.

### Q2_K_L - Q6_K_L 

[Qwen3.8-27B-tensor_types-Q6_K_L.txt](Qwen3.8-27B-tensor_types-Q6_K_L.txt)
```
llama-quantize \
  --allow-requantize \
  --tensor-type-file huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Qwen3.8-27B-tensor_types-Q6_K_L.txt \
  huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Huihui-Qwen3.8-27B-abliterated-bf16.gguf \
  huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Huihui-Qwen3.8-27B-abliterated-Q6_K_L.gguf Q6_K
```

### Q8_0_L  

[Qwen3.8-27B-tensor_types-Q8_0_L.txt](Qwen3.8-27B-tensor_types-Q8_0_L.txt)

```
llama-quantize \ 
  --allow-requantize \
  --tensor-type-file huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Qwen3.8-27B-tensor_types-Q8_0_L.txt \
  huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Huihui-Qwen3.8-27B-abliterated-bf16.gguf \ 
  huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Huihui-Qwen3.8-27B-abliterated-Q8_0_L.gguf Q8_0

```


## ollama

Please use the latest version of [ollama](https://github.com/ollama/ollama/releases)

You can use [huihui_ai/Qwen3.8-abliterated](https://ollama.com/huihui_ai/Qwen3.8-abliterated) directly, 
```
ollama run huihui_ai/Qwen3.8-abliterated
```


## llama.cpp
Use the latest [llama.cpp](https://github.com/ggml-org/llama.cpp),
```
llama-cli -m huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF/Huihui-Qwen3.8-27B-abliterated-Q4_K.gguf -c 262144
```
