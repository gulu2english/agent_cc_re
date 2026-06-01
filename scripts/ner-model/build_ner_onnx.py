"""
Fine-tune RoBERTa on CMeEE-V2 and export ONNX for Level 2 NER pipeline.

Usage:
    cd /home/gulu/agent_qc_cc
    python scripts/ner-model/build_ner_onnx.py

Output:
    knowledge/models/roberta-ner.onnx       — FP16 ONNX model
    knowledge/models/roberta-ner-fp32.onnx  — FP32 ONNX model
    knowledge/models/vocab.txt              — tokenizer vocabulary
"""

import json
import shutil
import numpy as np
from pathlib import Path

import torch
import torch.nn as nn
from torch.utils.data import Dataset as TorchDataset
from transformers import (
    AutoTokenizer, AutoModelForTokenClassification,
    Trainer, TrainingArguments, DataCollatorForTokenClassification,
    EarlyStoppingCallback,
)

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MODEL_SRC = REPO_ROOT / "chinese-roberta-wwm-ext"
CMEEE_DIR = MODEL_SRC / "CMeEE-V2"
OUTPUT_DIR = REPO_ROOT / "knowledge" / "models"
CHECKPOINT_DIR = OUTPUT_DIR / "cmeee-finetuned"

NER_LABELS = [
    "O",
    "B-dis", "I-dis",   # disease
    "B-sym", "I-sym",   # symptom
    "B-dru", "I-dru",   # drug
    "B-pro", "I-pro",   # procedure
    "B-equ", "I-equ",   # equipment
    "B-ite", "I-ite",   # imaging exam
    "B-bod", "I-bod",   # body/anatomy
    "B-dep", "I-dep",   # department
    "B-mic", "I-mic",   # microorganism
]
NUM_LABELS = len(NER_LABELS)
ID2LABEL = {i: l for i, l in enumerate(NER_LABELS)}
LABEL2ID = {l: i for i, l in enumerate(NER_LABELS)}
MAX_LENGTH = 256
BATCH_SIZE = 8  # Conservative for RTX 5060 8GB

# ── Data ────────────────────────────────────────────────

def load_cmeee(path: Path) -> list[dict]:
    with open(path, encoding="utf-8") as f:
        return json.load(f)

def spans_to_tags(text: str, entities: list[dict]) -> list[str]:
    tags = ["O"] * len(text)
    for ent in entities:
        t = ent["type"]
        if t not in {"dis","sym","dru","pro","equ","ite","bod","dep","mic"}:
            continue
        s = max(0, min(ent["start_idx"], len(text) - 1))
        e = max(s + 1, min(ent["end_idx"], len(text)))
        tags[s] = f"B-{t}"
        for i in range(s + 1, e):
            tags[i] = f"I-{t}"
    return tags

class CMeeeDataset(TorchDataset):
    def __init__(self, data: list[dict], tokenizer, max_length: int = MAX_LENGTH):
        self.items = []
        for item in data:
            text = item["text"]
            char_tags = spans_to_tags(text, item.get("entities", []))
            encoding = tokenizer(
                text, max_length=max_length, truncation=True, padding=False,
                return_offsets_mapping=True,
            )
            labels = []
            for off in encoding["offset_mapping"]:
                cs, ce = off
                if cs == 0 and ce == 0:
                    labels.append(-100)  # special tokens
                else:
                    tag = char_tags[cs] if cs < len(char_tags) else "O"
                    labels.append(LABEL2ID.get(tag, 0))

            self.items.append({
                "input_ids": encoding["input_ids"],
                "attention_mask": encoding["attention_mask"],
                "token_type_ids": encoding.get("token_type_ids", [0] * len(encoding["input_ids"])),
                "labels": labels,
            })

    def __len__(self):
        return len(self.items)

    def __getitem__(self, idx):
        item = self.items[idx]
        return {
            "input_ids": torch.tensor(item["input_ids"], dtype=torch.long),
            "attention_mask": torch.tensor(item["attention_mask"], dtype=torch.long),
            "token_type_ids": torch.tensor(item["token_type_ids"], dtype=torch.long),
            "labels": torch.tensor(item["labels"], dtype=torch.long),
        }

# ── Metrics ─────────────────────────────────────────────

def compute_metrics(p):
    preds, labels = p
    preds = np.argmax(preds, axis=2)

    tp = fp = fn = 0
    for pred_seq, label_seq in zip(preds, labels):
        pred_ents = set()
        true_ents = set()
        cur_p = cur_t = None

        for i in range(len(pred_seq)):
            p_tag = NER_LABELS[pred_seq[i]] if pred_seq[i] < NUM_LABELS else "O"
            t_tag = NER_LABELS[label_seq[i]] if label_seq[i] >= 0 and label_seq[i] < NUM_LABELS else "O"

            for cur, tag, ents, is_pred in [
                (cur_p, p_tag, pred_ents, True),
                (cur_t, t_tag, true_ents, False),
            ]:
                if tag.startswith("B-"):
                    if cur: ents.add((cur[0], cur[1], cur[2]))
                    cur_new = (i, i + 1, tag[2:])
                    if is_pred: cur_p = cur_new
                    else: cur_t = cur_new
                elif tag.startswith("I-") and cur and tag[2:] == cur[2]:
                    cur_new = (cur[0], i + 1, cur[2])
                    if is_pred: cur_p = cur_new
                    else: cur_t = cur_new
                else:
                    if cur: ents.add((cur[0], cur[1], cur[2]))
                    if is_pred: cur_p = None
                    else: cur_t = None

        if cur_p: pred_ents.add((cur_p[0], cur_p[1], cur_p[2]))
        if cur_t: true_ents.add((cur_t[0], cur_t[1], cur_t[2]))

        tp += len(pred_ents & true_ents)
        fp += len(pred_ents - true_ents)
        fn += len(true_ents - pred_ents)

    precision = tp / (tp + fp) if (tp + fp) > 0 else 0
    recall = tp / (tp + fn) if (tp + fn) > 0 else 0
    f1 = 2 * precision * recall / (precision + recall) if (precision + recall) > 0 else 0

    # Token-level accuracy
    flat_preds = [p for seq, lab in zip(preds, labels) for p, l in zip(seq, lab) if l != -100]
    flat_labels = [l for seq, lab in zip(preds, labels) for p, l in zip(seq, lab) if l != -100]
    acc = sum(1 for p, l in zip(flat_preds, flat_labels) if p == l) / len(flat_labels) if flat_labels else 0

    return {"f1": f1, "precision": precision, "recall": recall, "accuracy": acc}

# ── ONNX Export ─────────────────────────────────────────

class ExportWrapper(nn.Module):
    """Wraps HF model for ONNX export — encoder + dropout + classifier."""
    def __init__(self, hf_model):
        super().__init__()
        # chinese-roberta-wwm-ext uses BERT architecture internally
        self.encoder = hf_model.get_encoder() if hasattr(hf_model, 'get_encoder') else hf_model.bert
        self.dropout = hf_model.dropout
        self.classifier = hf_model.classifier

    def forward(self, input_ids, attention_mask, token_type_ids):
        out = self.encoder(
            input_ids=input_ids,
            attention_mask=attention_mask,
            token_type_ids=token_type_ids,
        )
        return self.classifier(self.dropout(out.last_hidden_state))

def export_onnx(model, out_path: Path, fp16: bool):
    wrapper = ExportWrapper(model)
    wrapper.eval()
    if fp16:
        wrapper = wrapper.half().cuda()
    else:
        wrapper = wrapper.cuda()

    dummy = (
        torch.zeros(1, 32, dtype=torch.long).cuda(),
        torch.ones(1, 32, dtype=torch.long).cuda(),
        torch.zeros(1, 32, dtype=torch.long).cuda(),
    )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper, dummy, str(out_path),
        input_names=["input_ids", "attention_mask", "token_type_ids"],
        output_names=["logits"],
        dynamic_axes={k: {0: "batch_size", 1: "sequence_length"}
                      for k in ["input_ids","attention_mask","token_type_ids","logits"]},
        opset_version=17,
        do_constant_folding=True,
    )
    print(f"  {out_path.name} ({out_path.stat().st_size / 1024**2:.1f} MB)")

# ── Main ────────────────────────────────────────────────

def main():
    print("=" * 60)
    print("CMeEE-V2 NER Fine-tuning + ONNX Export")
    print(f"Model: {MODEL_SRC}")
    print(f"Device: {'CUDA' if torch.cuda.is_available() else 'CPU'}")
    if torch.cuda.is_available():
        print(f"VRAM: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")
    print("=" * 60)

    # [1] Load data
    print("\n[1/5] Loading CMeEE-V2...")
    train = load_cmeee(CMEEE_DIR / "CMeEE-V2_train.json")
    dev = load_cmeee(CMEEE_DIR / "CMeEE-V2_dev.json")
    print(f"  train={len(train)}, dev={len(dev)}")

    # [2] Load model
    print("\n[2/5] Loading model...")
    tokenizer = AutoTokenizer.from_pretrained(str(MODEL_SRC))
    model = AutoModelForTokenClassification.from_pretrained(
        str(MODEL_SRC),
        num_labels=NUM_LABELS,
        id2label=ID2LABEL,
        label2id=LABEL2ID,
    )
    total = sum(p.numel() for p in model.parameters()) / 1e6
    print(f"  Params: {total:.1f}M")

    # [3] Tokenize
    print("\n[3/5] Tokenizing...")
    train_ds = CMeeeDataset(train, tokenizer)
    dev_ds = CMeeeDataset(dev, tokenizer)
    print(f"  train={len(train_ds)}, dev={len(dev_ds)}")

    # [4] Fine-tune
    print("\n[4/5] Fine-tuning...")
    args = TrainingArguments(
        output_dir=str(CHECKPOINT_DIR),
        eval_strategy="steps", eval_steps=300,
        save_strategy="steps", save_steps=300,
        learning_rate=3e-5,
        per_device_train_batch_size=BATCH_SIZE,
        per_device_eval_batch_size=BATCH_SIZE,
        num_train_epochs=5,
        weight_decay=0.01,
        warmup_ratio=0.1,
        logging_steps=50,
        load_best_model_at_end=True,
        metric_for_best_model="f1",
        greater_is_better=True,
        save_total_limit=2,
        fp16=True,
        report_to="none",
        dataloader_num_workers=0,
    )

    trainer = Trainer(
        model=model,
        args=args,
        train_dataset=train_ds,
        eval_dataset=dev_ds,
        data_collator=DataCollatorForTokenClassification(tokenizer, padding=True),
        compute_metrics=compute_metrics,
        callbacks=[EarlyStoppingCallback(early_stopping_patience=3)],
    )

    trainer.train()
    eval_out = trainer.evaluate()
    print(f"\n  Dev: f1={eval_out.get('eval_f1',0):.4f} acc={eval_out.get('eval_accuracy',0):.4f}")

    # [5] Export ONNX
    print("\n[5/5] Exporting ONNX...")
    model.eval().cuda()

    # Remove the HF model wrapper — extract roberta, dropout, classifier
    print("  FP16 ONNX...")
    export_onnx(model, OUTPUT_DIR / "roberta-ner.onnx", fp16=True)

    print("  FP32 ONNX (dev/debug)...")
    export_onnx(model, OUTPUT_DIR / "roberta-ner-fp32.onnx", fp16=False)

    # Copy vocab + config
    shutil.copy(MODEL_SRC / "vocab.txt", OUTPUT_DIR / "vocab.txt")
    json.dump({
        "vocab_size": tokenizer.vocab_size, "max_length": MAX_LENGTH,
        "pad_token_id": tokenizer.pad_token_id, "cls_token_id": tokenizer.cls_token_id,
        "sep_token_id": tokenizer.sep_token_id, "unk_token_id": tokenizer.unk_token_id,
        "label_list": NER_LABELS, "id2label": ID2LABEL, "label2id": LABEL2ID,
    }, open(OUTPUT_DIR / "roberta-ner-tokenizer.json", "w"), ensure_ascii=False, indent=2)

    model.save_pretrained(str(CHECKPOINT_DIR))
    tokenizer.save_pretrained(str(CHECKPOINT_DIR))

    print(f"\nDone. Files in {OUTPUT_DIR}:")
    for f in sorted(OUTPUT_DIR.iterdir()):
        s = f.stat().st_size
        tag = f"{s/1024**2:.0f}MB" if s > 1e6 else f"{s/1024:.0f}KB"
        print(f"  {f.name} ({tag})")

if __name__ == "__main__":
    main()
