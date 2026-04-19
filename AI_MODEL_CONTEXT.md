# AI_MODEL_CONTEXT: EMS IIoT Gateway

## 1. Overview
Current implementation focus is on deterministic edge processing (Virtual Tags via NCalc). However, the architecture is designed to support AI/ML inference at the edge as an optional extension.

## 2. Integration Point
- **Module:** `EMS.Gateway.EdgeRuleEngine` (M05).
- **Extension:** An `IMlInferenceAdapter` can be introduced to run parallel to the NCalc engine.
- **Data Input:** Receives `RawTagBatch` or `EnrichedTagBatch`.
- **Output:** Produces "AI-derived tags" with associated quality and confidence scores.

## 3. Technology Stack (Proposed)
- **Runtime:** ONNX Runtime for .NET.
- **Model Format:** ONNX (.onnx).
- **Execution Provider:** CPU (default), with potential for OpenVINO or CUDA if hardware permits.

## 4. Use Cases (Planned)
- **Anomaly Detection:** Real-time identification of abnormal energy consumption patterns.
- **Predictive Maintenance:** Analyzing motor vibration or temperature trends to predict failure.
- **Load Forecasting:** Short-term prediction of energy demand for Peak Shaving.

## 5. Performance Baselines (Target)
- **Inference Latency:** < 100ms per batch.
- **Memory Overhead:** < 200MB per active model.
