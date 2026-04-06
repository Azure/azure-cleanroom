# AI-Powered Spark Configuration Optimization

## Overview

This implementation adds AI-powered optimization for Spark driver and executor pod configurations using a locally deployed Llama 3.1 model via Kaito/AIKit.

## Architecture

```
User Request (useOptimizer=true)
        ↓
Spark Frontend API (/analytics/submitSqlJob)
        ↓
AI Optimizer Client
        ↓
Kaito Endpoint (http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc)
        ↓
Llama 3.1:8b-instruct Model
        ↓
Optimized Resource Configuration
        ↓
SparkApplication CR (with AI-optimized driver/executor resources)
```

## Components Modified

### 1. Configuration (`config/configuration.py`)
- **OptimizerSettings**: New configuration class for AI optimizer
  - `enabled`: Toggle AI optimization feature
  - `endpoint`: Kaito service endpoint (default: `http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc`)
  - `timeout`: Request timeout in seconds (default: 30)

### 2. Input Models (`models/input_models.py`)
- **SQLJobInput**: Added `use_optimizer` field (boolean)
  - When `true`, triggers AI optimization for that specific job

### 3. AI Optimizer Client (`clients/ai_optimizer_client.py`)
- **AIOptimizerClient**: New client for interacting with the AI model
- **get_optimized_config()**: Main method that:
  1. Builds a detailed prompt with query and dataset information
  2. Calls the Kaito endpoint using OpenAI-compatible API
  3. Parses the AI response (JSON format)
  4. Returns `OptimizedResourceConfig` with:
     - `driver_cores`, `driver_memory`
     - `executor_cores`, `executor_memory`
     - `executor_instances_min`, `executor_instances_max`
     - `reasoning` (explanation from AI)

### 4. Job Converters (`utilities/job_converters.py`)
- **SparkJobConverter.to_spark_spec()**: Added `override_sku_settings` parameter
- **SQLSparkJobConverter.to_spark_spec()**: Uses override settings when provided

### 5. Main API (`main.py`)
- **submit_sql_job()**: Enhanced workflow:
  1. Check if `config.service.optimizer.enabled` AND `job.use_optimizer`
  2. If yes, call AIOptimizerClient with query and dataset info
  3. If AI returns valid config, create override `SkuSettings`
  4. Pass override settings to job converter
  5. Submit SparkApplication with AI-optimized resources

### 6. Helm Configuration (`spark-frontend/values.app.yaml`)
- Added `optimizer` section with default Kaito endpoint

## Usage

### Enable AI Optimization

1. **Global Enable** (in helm values):
```yaml
optimizer:
  enabled: true
  endpoint: "http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc"
  timeout: 30
```

2. **Per-Job Enable** (in API request):
```json
{
  "query": "SELECT * FROM data WHERE amount > 1000",
  "datasets": [...],
  "datasink": {...},
  "useOptimizer": true  // <-- Enable AI optimization for this job
}
```

### Example API Request

```bash
curl -X POST http://spark-frontend:8000/analytics/submitSqlJob \
  -H "Content-Type: application/json" \
  -d '{
    "contractId": "contract-123",
    "query": "SELECT region, SUM(sales) FROM transactions GROUP BY region",
    "datasets": [
      {
        "name": "transactions",
        "viewName": "transactions_view",
        "format": "parquet",
        "schema": {...}
      }
    ],
    "datasink": {
      "name": "results",
      "viewName": "results_view",
      "format": "parquet"
    },
    "useOptimizer": true
  }'
```

## AI Prompt Structure

The AI model receives a prompt containing:

1. **SQL Query**: The actual query to be executed
2. **Dataset Information**: Number of datasets, formats, field counts
3. **Expected Output Format**: JSON schema with specific fields
4. **Example Response**: Reference format for the AI

The AI responds with:
```json
{
  "driverCores": 2.0,
  "driverMemory": "2g",
  "executorCores": 2.0,
  "executorMemory": "4g",
  "executorInstancesMin": 2,
  "executorInstancesMax": 4,
  "reasoning": "Medium query complexity with aggregations requiring moderate resources"
}
```

## Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ 1. User submits SQL job with useOptimizer=true                  │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 2. submit_sql_job() checks config.service.optimizer.enabled     │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 3. AIOptimizerClient builds prompt with query + dataset info    │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 4. POST to /v1/chat/completions on Kaito endpoint               │
│    Model: llama3.1:8b-instruct                                  │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 5. Parse AI response → OptimizedResourceConfig                  │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 6. Create SkuSettings with AI-recommended resources             │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 7. Build SparkApplication CR with optimized driver/executor     │
└────────────────────────────┬────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│ 8. Submit to Kubernetes via Spark Operator                      │
└─────────────────────────────────────────────────────────────────┘
```

## Error Handling

- If AI endpoint is unreachable or times out → Falls back to default SKU settings
- If AI response is invalid JSON → Falls back to default SKU settings
- If AI optimization is disabled globally → Ignores `useOptimizer` parameter
- All errors are logged with traceback for debugging

## Logging

The implementation includes comprehensive logging:
- AI optimizer requests and responses
- Parsed configurations with reasoning
- Fallback decisions when AI fails
- Resource comparison (AI vs. default)

Example logs:
```
INFO: Using AI optimizer for job 1705234567
INFO: Calling AI optimizer at http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc/v1/chat/completions
INFO: AI optimizer response: {"driverCores": 2.0, ...}
INFO: Successfully parsed AI config: driver=2.0c/2g, executor=2.0c/4g (instances: 2-4)
INFO: AI reasoning: Medium query complexity with aggregations
INFO: AI optimizer recommended: driver=2.0c/2g, executor=2.0c/4g (instances: 2-4)
```

## Configuration Reference

### Optimizer Settings in values.yaml

```yaml
optimizer:
  enabled: true                    # Enable/disable AI optimization globally
  endpoint: "http://..."          # Kaito endpoint URL
  timeout: 30                     # Request timeout in seconds
```

### Environment Variable Override

```bash
AI_OPTIMIZER_ENDPOINT="http://custom-endpoint:5000"
```

## Benefits

1. **Dynamic Resource Allocation**: AI analyzes query complexity and recommends appropriate resources
2. **Cost Optimization**: Avoid over-provisioning by right-sizing driver/executor pods
3. **Performance Optimization**: Better resource allocation for query patterns
4. **Flexibility**: Can be enabled/disabled globally or per-job
5. **Graceful Degradation**: Falls back to default settings if AI fails

## Future Enhancements

- [ ] Cache AI recommendations for similar queries
- [ ] Learning from historical job performance metrics
- [ ] Integration with Prometheus metrics for feedback loop
- [ ] Custom model fine-tuning based on cluster-specific patterns
