# RoBERTa NER 模型训练与导出指南

## 环境

- Python 3.10+
- CUDA GPU（RTX 5060/4090）
- 已安装：torch, transformers, onnx, onnxruntime

## 文件位置

| 路径 | 说明 |
|------|------|
| `chinese-roberta-wwm-ext/` | 预训练模型 |
| `chinese-roberta-wwm-ext/CMeEE-V2/` | 训练数据 (15000条) |
| `knowledge/models/cmeee-finetuned/v2-final/` | 当前最优 checkpoint (F1=0.52) |
| `knowledge/models/roberta-ner.onnx` | 导出的 ONNX 模型 (FP16) |
| `scripts/ner-model/build_ner_onnx.py` | 训练+导出脚本 |

## 继续训练

```bash
cd /home/gulu/agent_qc_cc
python scripts/ner-model/build_ner_onnx.py
```

脚本会自动：
1. 从 `v2-final/` checkpoint 加载模型继续训练
2. 8 epochs, batch_size=12, FP16
3. 训练完成后自动导出 ONNX 到 `knowledge/models/`

## 训练参数调优（可选）

编辑 `scripts/ner-model/build_ner_onnx.py`，搜索 `TrainingArguments`，修改：

```python
num_train_epochs=12,       # 增加训练轮数
learning_rate=1e-5,        # 降低学习率（后期微调）
per_device_train_batch_size=16,  # 增大 batch size（显存够的话）
```

## 监控指标

训练过程中关注 `eval_f1` 和 `eval_accuracy`：

| F1 范围 | 状态 |
|---------|------|
| < 0.55 | 欠拟合，继续训练 |
| 0.55-0.65 | 可接受 |
| 0.65-0.75 | 良好 |
| > 0.75 | 优秀 |

## 测试ONNX模型

```bash
python3 -c "
import numpy as np, onnxruntime as ort
from transformers import AutoTokenizer
tok = AutoTokenizer.from_pretrained('chinese-roberta-wwm-ext')
s = ort.InferenceSession('knowledge/models/roberta-ner.onnx', providers=['CPUExecutionProvider'])
text = '右肺上叶见结节'
t = tok(text, return_tensors='np', max_length=256, truncation=True)
logits = s.run(['logits'], {'input_ids':t['input_ids'], 'attention_mask':t['attention_mask'], 'token_type_ids':np.zeros_like(t['input_ids'])})[0]
print('OK — shape:', logits.shape)
"
```

## 训练完成后

```bash
# 更新 C# 项目使用的模型文件（脚本已自动更新）
# 重新构建并运行测试
dotnet build src/Agent_QC/Agent_QC.sln -c Release
dotnet test src/Agent_QC/tests/Agent_QC.Tests.csproj -c Release --filter "FullyQualifiedName!~VllmIntegration"
```
