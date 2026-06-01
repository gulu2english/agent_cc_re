"""
Fine-tune the RoBERTa NER model on CMeEE v2 dataset.

Uses the chinese-roberta-wwm-ext checkpoint and fine-tunes a token
classification head for 9 Chinese medical entity types.

Prerequisites:
    pip install torch transformers datasets seqeval accelerate

Usage:
    python scripts/ner-model/finetune_cmeee.py

Output:
    knowledge/models/cmeee-finetuned/  — fine-tuned PyTorch checkpoint
    Then run build_ner_onnx.py with the fine-tuned weights to export ONNX.
"""

import os
import sys
from pathlib import Path
import json

import torch
import numpy as np
from transformers import (
    AutoTokenizer, AutoModelForTokenClassification,
    Trainer, TrainingArguments, DataCollatorForTokenClassification,
    EarlyStoppingCallback,
)
from datasets import Dataset, DatasetDict

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MODEL_SRC = REPO_ROOT / "chinese-roberta-wwm-ext"
OUTPUT_DIR = REPO_ROOT / "knowledge" / "models" / "cmeee-finetuned"

# CMeEE label set
LABEL_LIST = [
    "O",
    "B-dis", "I-dis",   # 疾病 disease
    "B-sym", "I-sym",   # 临床表现 symptom
    "B-dru", "I-dru",   # 药物 drug
    "B-pro", "I-pro",   # 医疗程序 procedure
    "B-equ", "I-equ",   # 医疗设备 equipment
    "B-ite", "I-ite",   # 检查 imaging test
    "B-bod", "I-bod",   # 身体部位 body
    "B-dep", "I-dep",   # 科室 department
    "B-mic", "I-mic",   # 微生物 microorganism
]

ID2LABEL = {i: l for i, l in enumerate(LABEL_LIST)}
LABEL2ID = {l: i for i, l in enumerate(LABEL_LIST)}

# Max sequence length (Chinese medical reports are concise)
MAX_LENGTH = 256

# ── CMeEE v2 data ──────────────────────────────────────

def load_cmeee_data():
    """
    Load CMeEE v2 from HuggingFace datasets hub.

    If not available, downloads from:
    https://huggingface.co/datasets/qgyd2021/CMeEE-V2

    Returns DatasetDict with train/validation/test splits.
    """
    from datasets import load_dataset

    print("Loading CMeEE v2 dataset from HuggingFace...")
    try:
        dataset = load_dataset("qgyd2021/CMeEE-V2")
        print(f"  train: {len(dataset['train'])} sentences")
        print(f"  validation: {len(dataset['validation'])} sentences")
        print(f"  test: {len(dataset['test'])} sentences")
        return dataset
    except Exception as e:
        print(f"Error loading CMeEE-V2: {e}")
        print()
        print("Manual download steps:")
        print("  1. Visit https://huggingface.co/datasets/qgyd2021/CMeEE-V2")
        print("  2. Download the dataset files")
        print("  3. Place them in data/cmeee-v2/")
        print()
        print("Or use a custom dataset by placing JSON files in data/cmeee-v2/")
        print("  Format: [{'text': '...', 'entities': [{'start':0,'end':3,'type':'bod'},...]}, ...]")
        sys.exit(1)


def load_custom_data(data_dir: Path):
    """Load custom JSON data as fallback."""
    dataset = {}
    for split in ["train", "validation", "test"]:
        path = data_dir / f"{split}.json"
        if path.exists():
            with open(path) as f:
                data = json.load(f)
            dataset[split] = Dataset.from_list(data)
            print(f"  {split}: {len(dataset[split])} sentences (custom)")
    return DatasetDict(dataset)


# ── Tokenization ───────────────────────────────────────

def tokenize_and_align_labels(examples, tokenizer):
    """Tokenize sentences and align BIO labels to subword tokens."""
    tokenized_inputs = tokenizer(
        examples["text"],
        max_length=MAX_LENGTH,
        truncation=True,
        padding=False,
        is_split_into_words=False,  # Whole sentence input
    )

    all_labels = []
    for i, entities in enumerate(examples["entities"]):
        # Convert entity spans to BIO tags
        word_ids = tokenized_inputs.word_ids(batch_index=i)
        label_ids = [LABEL2ID["O"]] * len(word_ids)

        for entity in entities:
            start = entity["start"]
            end = entity["end"]
            entity_type = entity["type"]
            b_tag = f"B-{entity_type}"
            i_tag = f"I-{entity_type}"

            if b_tag not in LABEL2ID:
                continue

            # Find tokens covering this entity span
            for j, wid in enumerate(word_ids):
                if wid is not None and start <= wid < end:
                    label_ids[j] = LABEL2ID[b_tag if wid == start else i_tag]

        all_labels.append(label_ids)

    tokenized_inputs["labels"] = all_labels
    return tokenized_inputs


def tokenize_simple(examples, tokenizer):
    """Simple tokenization when entities are pre-aligned BIO tags."""
    tokenized_inputs = tokenizer(
        examples["tokens"],
        max_length=MAX_LENGTH,
        truncation=True,
        padding=False,
        is_split_into_words=True,
    )

    labels = []
    for i, bio_tags in enumerate(examples["ner_tags"]):
        word_ids = tokenized_inputs.word_ids(batch_index=i)
        label_ids = []
        prev_wid = None
        for wid in word_ids:
            if wid is None:
                label_ids.append(-100)
            elif wid != prev_wid:
                label_ids.append(bio_tags[wid])
            else:
                # Subword of same word — use I- tag if word is B-
                tag = bio_tags[wid]
                label_ids.append(LABEL2ID.get(f"I-{LABEL_LIST[tag][2:]}", tag)
                                 if LABEL_LIST[tag].startswith("B-") else tag)
            prev_wid = wid
        labels.append(label_ids)

    tokenized_inputs["labels"] = labels
    return tokenized_inputs


# ── Training ───────────────────────────────────────────

def compute_metrics(p):
    """Compute F1 score using seqeval."""
    from seqeval.metrics import f1_score, precision_score, recall_score

    predictions, labels = p
    predictions = np.argmax(predictions, axis=2)

    true_labels = [[LABEL_LIST[l] for l in label if l != -100] for label in labels]
    pred_labels = [[LABEL_LIST[p] for p, l in zip(pred, label) if l != -100]
                   for pred, label in zip(predictions, labels)]

    return {
        "precision": precision_score(true_labels, pred_labels),
        "recall": recall_score(true_labels, pred_labels),
        "f1": f1_score(true_labels, pred_labels),
    }


def main():
    print("=" * 60)
    print("CMeEE NER Fine-tuning")
    print(f"Model: {MODEL_SRC}")
    print(f"Output: {OUTPUT_DIR}")
    print("=" * 60)

    # Load tokenizer and model
    tokenizer = AutoTokenizer.from_pretrained(str(MODEL_SRC))
    model = AutoModelForTokenClassification.from_pretrained(
        str(MODEL_SRC),
        num_labels=len(LABEL_LIST),
        id2label=ID2LABEL,
        label2id=LABEL2ID,
    )

    # Try loading CMeEE from HuggingFace, fall back to custom
    try:
        dataset = load_cmeee_data()
    except SystemExit:
        custom_dir = REPO_ROOT / "data" / "cmeee-v2"
        if custom_dir.exists():
            dataset = load_custom_data(custom_dir)
        else:
            print(f"No dataset found. Place CMeEE JSON files in {custom_dir}")
            sys.exit(1)

    # Tokenize
    print("\nTokenizing...")
    tokenized_dataset = dataset.map(
        lambda x: tokenize_and_align_labels(x, tokenizer),
        batched=False,
        remove_columns=dataset["train"].column_names,
    )

    data_collator = DataCollatorForTokenClassification(tokenizer)

    # Training arguments
    training_args = TrainingArguments(
        output_dir=str(OUTPUT_DIR),
        eval_strategy="epoch",
        save_strategy="epoch",
        learning_rate=2e-5,
        per_device_train_batch_size=16,
        per_device_eval_batch_size=16,
        num_train_epochs=10,
        weight_decay=0.01,
        warmup_ratio=0.1,
        logging_steps=50,
        load_best_model_at_end=True,
        metric_for_best_model="f1",
        greater_is_better=True,
        save_total_limit=2,
        fp16=True,
        report_to="none",
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=tokenized_dataset["train"],
        eval_dataset=tokenized_dataset.get("validation", tokenized_dataset["test"]),
        data_collator=data_collator,
        compute_metrics=compute_metrics,
        callbacks=[EarlyStoppingCallback(early_stopping_patience=3)],
    )

    # Train
    print("\nStarting training...")
    trainer.train()

    # Evaluate
    print("\nEvaluating...")
    eval_results = trainer.evaluate()
    print(f"Evaluation results: {eval_results}")

    # Save
    print(f"\nSaving to {OUTPUT_DIR}...")
    model.save_pretrained(str(OUTPUT_DIR))
    tokenizer.save_pretrained(str(OUTPUT_DIR))

    print()
    print("Done! Now export to ONNX:")
    print("  python scripts/ner-model/build_ner_onnx.py --model-dir knowledge/models/cmeee-finetuned")


if __name__ == "__main__":
    main()
