# RoBERTa NER + LogicEngine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Level 2 RoBERTa NER entity extraction + LINQ logic engine to the QC pipeline.

**Architecture:** RobertaNerService (ONNX + fallback) extracts entities → EntityNormalizer maps synonyms → LogicEngine runs 4 comparators. All registered as singletons in Program.cs, called from QcService.

**Tech Stack:** C#/.NET 8, Microsoft.ML.OnnxRuntime.Gpu, terminology.yaml, jieba.NET

---

### Task 1: NerEntity model

**Files:** Create `src/Agent_QC/src/Models/NerEntity.cs`

Create the entity record type used by all Level 2 components.

### Task 2: EntityNormalizer

**Files:** Create `src/Agent_QC/src/Services/EntityNormalizer.cs`

Loads terminology.yaml at startup. Maps non-standard terms → canonical forms. Deduplicates overlapping entity spans (keep highest confidence, longest span).

### Task 3: LogicEngine

**Files:** Create `src/Agent_QC/src/Services/LogicEngine.cs`

4 comparators: DirectionConflict (left/right mismatch), GenderAnatomyConflict (gender ↔ anatomy), SiteOmission (findings sites missing from impression), ExamConsistency (exam entity vs request field).

### Task 4: RobertaNerService

**Files:** Create `src/Agent_QC/src/Services/RobertaNerService.cs`

ONNX inference wrapper. Startup: check GPU + model → load ONNX or set dictionary fallback. Extract() returns List<NerEntity>. Dictionary fallback uses jieba + terminology.yaml + regex for measures.

### Task 5: QcService integration + Program.cs registration

**Files:** Modify `src/Agent_QC/src/Services/QcService.cs`, `src/Agent_QC/src/Program.cs`, `src/Agent_QC/src/Agent_QC.csproj`

Add Level 2 block between RuleEngine and Skill Squad. Register 3 new services as singletons. Add Microsoft.ML.OnnxRuntime.Gpu package.

### Task 6: Tests

**Files:** Create `tests/UnitTests/Services/EntityNormalizerTests.cs`, `tests/UnitTests/Services/LogicEngineTests.cs`, `tests/UnitTests/Services/RobertaNerServiceTests.cs`

EntityNormalizer: synonym mapping, dedup, empty input. LogicEngine: each comparator (happy path, no conflict, empty/nulls). RobertaNerService: dictionary fallback extraction of anatomy/direction/finding/measure.

### Task 7: Verify

Build, run tests, confirm 0 new failures.
