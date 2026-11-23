using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Onboarding;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class OnboardingController
{
    private static string GetComputerSampleNote()
    {
        return """
# AI Knowledge Introduction

## What is Artificial Intelligence

Artificial Intelligence (AI) is a branch of computer science focused on creating systems that can perform tasks requiring human intelligence.

## Large Language Models (LLM)

Large Language Models are among the most popular AI technologies today:

- **GPT Series**: Developed by OpenAI, widely used for conversation, writing, and coding assistance
- **Claude**: Developed by Anthropic, known for long context and safety
- **DeepSeek**: Chinese-developed LLM, strong reasoning ability, high cost-effectiveness
- **Qwen (Tongyi Qianwen)**: Developed by Alibaba Cloud, excellent Chinese comprehension

## How to Use AI for Learning

1. **Question-based learning**: Describe concepts you don't understand in natural language and ask AI to explain in simple terms
2. **Note organization**: Let AI help organize scattered knowledge into structured learning notes
3. **Knowledge testing**: Ask AI to quiz you to test learning outcomes
4. **Analogy understanding**: Ask AI to explain abstract concepts using everyday analogies

## Prompt Engineering Tips

- **Set a role**: "You are an experienced teacher"
- **Provide context**: "I have no programming background, please explain in simple terms"
- **Specify format**: "Please output in Markdown list format"
- **Step-by-step**: Break complex problems into smaller questions

---

> 💡 **Family Learning Tip**: Use the computer knowledge base as your family's tech reference center. Record and look up computer issues, new software tutorials, or AI news here.
""";
    }

    private static string GetTcmSampleNote()
    {
        return """
# Spleen-Stomach Disease Knowledge Notes

## Understanding the Spleen and Stomach

According to Traditional Chinese Medicine (TCM), the spleen and stomach are "the foundation of postnatal existence and the source of qi and blood production." When the spleen and stomach function properly, the body can digest and absorb nutrients from food, transforming them into qi, blood, and body fluids.

## Common Spleen-Stomach Pattern Types

### 1. Spleen-Stomach Qi Deficiency
**Symptoms**: Poor appetite, abdominal distension, loose stools, fatigue, sallow complexion
**Treatment**: Fortify the spleen and boost qi
**Common Formulas**: Four Gentlemen Decoction (Si Jun Zi Tang), Center-Supplementing Qi-Boosting Decoction (Bu Zhong Yi Qi Tang)

### 2. Spleen-Stomach Yang Deficiency with Cold
**Symptoms**: Cold pain in the stomach, preference for warmth and pressure, fear of cold, cold extremities, loose stools
**Treatment**: Warm the middle and fortify the spleen
**Common Formulas**: Center-Rectifying Pill (Li Zhong Wan), Prepared Aconite Center-Rectifying Pill (Fu Zi Li Zhong Wan)

### 3. Spleen-Stomach Damp-Heat
**Symptoms**: Epigastric fullness, bitter taste and bad breath, sticky stools, yellow greasy tongue coating
**Treatment**: Clear heat and drain dampness
**Common Formulas**: Three Kernels Decoction (San Ren Tang), Coptis and Officinalis Magnolia Bark Beverage (Lian Po Yin)

### 4. Liver Depression and Spleen Deficiency
**Symptoms**: Chest and rib-side distension, emotional discomfort, abdominal pain leading to diarrhea, pain relief after diarrhea
**Treatment**: Course the liver and fortify the spleen
**Common Formulas**: Pain-Diarrhea Essential Formula (Tong Xie Yao Fang), Free and Easy Wanderer Powder (Xiao Yao San)

## Daily Stomach Care Tips

| Do | Don't |
|---|---|
| Eat at regular times | Overeat or binge eat |
| Chew thoroughly | Eat too quickly |
| Eat warm foods | Eat extremely hot or cold foods |
| Maintain emotional balance | Worry or get angry excessively |
| Moderate exercise | Sedentary lifestyle |

## Classic Formula Quick Reference

- **Four Gentlemen Decoction**: Ginseng, Atractylodes, Poria, Licorice → Basic formula for supplementing qi and strengthening the spleen
- **Center-Rectifying Pill**: Ginseng, Dried Ginger, Atractylodes, Licorice → Warms the middle and dispels cold
- **Six Gentlemen Decoction with Aucklandia and Amomum**: Four Gentlemen + Costus Root, Amomum, Tangerine Peel, Pinellia → Fortifies the spleen, harmonizes the stomach, moves qi and transforms phlegm

---

> 🌿 **Family Health Tip**: Spleen-stomach care starts with daily habits. Eat a good breakfast, a full lunch, and a light dinner. Walk a hundred steps after meals. Maintain a happy mood, as "worrying injures the spleen" — excessive thinking can impair spleen-stomach function.
""";
    }
}
