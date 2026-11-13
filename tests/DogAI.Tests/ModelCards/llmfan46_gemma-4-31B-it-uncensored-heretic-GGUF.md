---
library_name: transformers
license: apache-2.0
license_link: https://ai.google.dev/gemma/docs/gemma_4_license
pipeline_tag: image-text-to-text
tags:
- heretic
- uncensored
- decensored
- abliterated
- ara
base_model:
- llmfan46/gemma-4-31B-it-uncensored-heretic
---
<div style="background-color: #ff4444; color: white; padding: 20px; border-radius: 10px; text-align: center; margin: 20px 0;">
<h2 style="color: white; margin: 0 0 10px 0;">🚨⚠️ I HAVE REACHED HUGGING FACE'S FREE STORAGE LIMIT ⚠️🚨</h2>
<p style="font-size: 18px; margin: 0 0 15px 0;">I can no longer upload new models unless I can cover the cost of additional storage.<br>I host <b>70+ free models</b> as an independent contributor and this work is unpaid.<br><b>Without your support, no more new models can be uploaded.</b></p>
<p style="font-size: 20px; margin: 0;">
<a href="https://patreon.com/LLMfan46" style="color: white; text-decoration: underline;">🎉 Patreon (Monthly)</a> &nbsp;|&nbsp;
<a href="https://ko-fi.com/llmfan46" style="color: white; text-decoration: underline;">☕ Ko-fi (One-time)</a>
</p>
<p style="font-size: 16px; margin: 10px 0 0 0;">Every contribution goes directly toward Hugging Face storage fees to keep models free for everyone.</p>
</div>

---

### **90% fewer refusals** (10/100 Uncensored vs 99/100 Original) while preserving model quality (0.0541 KL divergence).

## ❤️ Support My Work
Creating these models takes significant time, work and compute. If you find them useful consider supporting me:

![image/png](https://huggingface.co/llmfan46/Omega-Darker-Gaslight_The-Final-Forgotten-Fever-Dream-24B-ultra-uncensored-heretic-v1/resolve/main/waifu001.webp)

| Platform | Link | What you get |
|----------|------|--------------|
| 🎉 Patreon | [Monthly support](https://patreon.com/LLMfan46) | Priority model requests |
| ☕ Ko-fi | [One-time tip](https://ko-fi.com/llmfan46) | My eternal gratitude |

Your help will motivate me and would go into further improving my workflow and coverings fees for storage, compute and may even help uncensoring bigger model with rental Cloud GPUs.

-----

GGUF quantizations of [llmfan46/gemma-4-31B-it-uncensored-heretic](https://huggingface.co/llmfan46/gemma-4-31B-it-uncensored-heretic).

# This is a decensored version of [google/gemma-4-31B-it](https://huggingface.co/google/gemma-4-31B-it), made using [Heretic](https://github.com/p-e-w/heretic) v1.2.0 with the [Arbitrary-Rank Ablation (ARA)](https://github.com/p-e-w/heretic/pull/211) method

## Abliteration parameters

| Parameter | Value |
| :-------- | :---: |
| **start_layer_index** | 30 |
| **end_layer_index** | 48 |
| **preserve_good_behavior_weight** | 0.5437 |
| **steer_bad_behavior_weight** | 0.0005 |
| **overcorrect_relative_weight** | 0.9949 |
| **neighbor_count** | 15 |

## Targeted components

  * attn.o_proj

## Performance

| Metric | This model | Original model ([gemma-4-31B-it](https://huggingface.co/google/gemma-4-31B-it)) |
| :----- | :--------: | :---------------------------: |
| **KL divergence** | <span style="color:darkgoldenrod">0.0541</span> | 0 *(by definition)* |
| **Refusals** | ✅ <span style="color:darkgreen">10/100</span> | ❌ <span style="color:blue">99/100</span> |

Lower refusals indicate fewer content restrictions, while lower KL divergence indicates more closeness to the original model's baseline. Higher refusals cause more rejections, objections, pushbacks, lecturing, censorship, softening and deflections.

## MMLU test results:

<span style="color:blue">Original:</span>

============================================================

- Total questions: 7021

- Correct: 6073

- Accuracy: 0.8650 (86.50%)

- Parse failures: 52

============================================================

Tested subject scores:
- professional_law: 0.7682 (603/785)
- moral_scenarios: 0.8348 (369/442)
- miscellaneous: 0.9243 (354/383)
- professional_psychology: 0.9051 (286/316)
- high_school_psychology: 0.9630 (260/270)
- high_school_macroeconomics: 0.9289 (183/197)
- elementary_mathematics: 0.9348 (172/184)
- moral_disputes: 0.8448 (147/174)
- prehistory: 0.9360 (161/172)
- philosophy: 0.8491 (135/159)
- high_school_biology: 0.9539 (145/152)
- professional_accounting: 0.8392 (120/143)
- clinical_knowledge: 0.9143 (128/140)
- high_school_microeconomics: 0.9706 (132/136)
- nutrition: 0.9259 (125/135)
- professional_medicine: 0.9328 (125/134)
- conceptual_physics: 0.9219 (118/128)
- high_school_mathematics: 0.5354 (68/127)
- human_aging: 0.8448 (98/116)
- security_studies: 0.8750 (98/112)
- high_school_statistics: 0.8739 (97/111)
- marketing: 0.9725 (106/109)
- high_school_world_history: 0.9528 (101/106)
- sociology: 0.8932 (92/103)
- high_school_government_and_politics: 0.9703 (98/101)
- high_school_geography: 0.9293 (92/99)
- high_school_chemistry: 0.7732 (75/97)
- high_school_us_history: 0.9368 (89/95)
- virology: 0.5169 (46/89)
- college_medicine: 0.8523 (75/88)
- world_religions: 0.9205 (81/88)
- high_school_physics: 0.7976 (67/84)
- electrical_engineering: 0.8642 (70/81)
- astronomy: 0.9494 (75/79)
- logical_fallacies: 0.8816 (67/76)
- high_school_european_history: 0.9041 (66/73)
- anatomy: 0.8732 (62/71)
- college_biology: 0.9688 (62/64)
- human_sexuality: 0.9219 (59/64)
- formal_logic: 0.7969 (51/64)
- public_relations: 0.7377 (45/61)
- international_law: 0.9167 (55/60)
- college_physics: 0.6842 (39/57)
- college_mathematics: 0.7455 (41/55)
- econometrics: 0.7963 (43/54)
- jurisprudence: 0.8679 (46/53)
- high_school_computer_science: 0.9808 (51/52)
- machine_learning: 0.8462 (44/52)
- medical_genetics: 0.9608 (49/51)
- global_facts: 0.6078 (31/51)
- management: 0.9200 (46/50)
- us_foreign_policy: 0.9200 (46/50)
- college_chemistry: 0.5745 (27/47)
