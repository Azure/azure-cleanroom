# Event Generator

This code generator creates type-safe factory methods for events used in the analytics workload. It reads event definitions from JSON files and generates Python code with enums, data classes, and factory methods.

## What It Generates

The generator creates three types of event code:

1. **Operational Events** - Events for tracking operational activities during query execution
2. **Audit Records** - Events for audit logging and compliance tracking
3. **Statistics Events** - Events for recording query execution statistics

For each event type, the generator produces:
- An enum type for event names
- Factory methods to create event instances
- Type-safe parameters based on the event definitions
- (Statistics only) Pydantic data classes for structured event data

## Input JSON Format

### Operational Events

Expected format in JSON file:

```json
{
    "EVENT_NAME": {
        "id": 1001,
        "message": "Event message with {parameter1} and {parameter2}"
    },
    "ANOTHER_EVENT": {
        "id": 1002,
        "message": "Simple message without parameters"
    }
}
```

**Fields:**
- `id`: Unique integer identifier for the event
- `message`: Message template with optional `{parameter}` placeholders

### Audit Records

Expected format in JSON file:

```json
{
    "AUDIT_EVENT_NAME": {
        "id": 3001,
        "message": "Audit message with {parameter1}"
    },
    "ANOTHER_AUDIT_EVENT": {
        "id": 3002,
        "message": "Audit action performed by {user}"
    }
}
```

**Fields:**
- `id`: Unique integer identifier for the audit record
- `message`: Message template with optional `{parameter}` placeholders

**Note:** Audit records automatically include a `source` parameter in addition to message parameters.

### Statistics Events

Expected format in JSON file:

```json
{
    "QUERY_STATISTICS": {
        "id": 2001,
        "properties": {
            "duration_sec": "float",
            "num_rows_read": "integer",
            "num_rows_written": "integer"
        }
    }
}
```

**Fields:**
- `id`: Unique integer identifier for the statistics event
- `properties`: Dictionary mapping property names to types

**Supported property types:**
- `"integer"` → `int`
- `"float"` → `float`
- `"string"` → `str`
- `"boolean"` → `bool`

## Generated Python Code

### Operational Events Output

Generated file contains:

```python
import enum
from analytics_contracts.events import OperationalEvent

class OperationalEventType(enum.Enum):
    """Enum for operational event types."""
    EVENT_NAME = "EVENT_NAME"
    ANOTHER_EVENT = "ANOTHER_EVENT"

class OperationalEventFactory:
    """Factory class for OperationalEvent instances."""
    
    @staticmethod
    def EventName(parameter1: str, parameter2: str) -> OperationalEvent:
        """Create EVENT_NAME event."""
        return OperationalEvent(
            id=1001,
            name=OperationalEventType.EVENT_NAME.value,
            message="Event message with {parameter1} and {parameter2}",
            parameters={"parameter1": parameter1, "parameter2": parameter2},
        )
    
    @staticmethod
    def AnotherEvent() -> OperationalEvent:
        """Create ANOTHER_EVENT event."""
        return OperationalEvent(
            id=1002,
            name=OperationalEventType.ANOTHER_EVENT.value,
            message="Simple message without parameters",
            parameters={},
        )
```

### Audit Records Output

Generated file contains:

```python
import enum
from analytics_contracts.audit import AuditRecord

class AuditRecordType(enum.Enum):
    """Enum for audit record types."""
    AUDIT_EVENT_NAME = "AUDIT_EVENT_NAME"
    ANOTHER_AUDIT_EVENT = "ANOTHER_AUDIT_EVENT"

class AuditRecordFactory:
    """Factory class for AuditRecord instances."""
    
    @staticmethod
    def AuditEventName(source: str, parameter1: str) -> AuditRecord:
        """Create AUDIT_EVENT_NAME event."""
        return AuditRecord(
            id=3001,
            name=AuditRecordType.AUDIT_EVENT_NAME.value,
            message="Audit message with {parameter1}",
            source=source,
            parameters={"parameter1": parameter1},
        )
    
    @staticmethod
    def AnotherAuditEvent(source: str, user: str) -> AuditRecord:
        """Create ANOTHER_AUDIT_EVENT event."""
        return AuditRecord(
            id=3002,
            name=AuditRecordType.ANOTHER_AUDIT_EVENT.value,
            message="Audit action performed by {user}",
            source=source,
            parameters={"user": user},
        )
```

### Statistics Events Output

Generated file contains:

```python
import enum
from pydantic import BaseModel, Field
from analytics_contracts.statistics.statistics_event import StatisticsEvent

class StatisticsEventType(enum.Enum):
    """Enum for statistics event types."""
    QUERY_STATISTICS = "QUERY_STATISTICS"

class QueryStatisticsData(BaseModel):
    duration_sec: float = Field(description="Duration Sec", default=0.0)
    num_rows_read: int = Field(description="Num Rows Read", default=0)
    num_rows_written: int = Field(description="Num Rows Written", default=0)

class StatisticsEventFactory:
    """Factory class for StatisticsEvent instances."""
    
    @staticmethod
    def QueryStatistics(duration_sec: float, num_rows_read: int, num_rows_written: int) -> StatisticsEvent:
        """Create QUERY_STATISTICS statistics event."""
        import base64
        import json

        data = QueryStatisticsData(
            duration_sec=duration_sec,
            num_rows_read=num_rows_read,
            num_rows_written=num_rows_written
        ).dict()
        data_json = json.dumps(data)
        data_base64 = base64.b64encode(data_json.encode("utf-8")).decode("utf-8")

        return StatisticsEvent(
            type=StatisticsEventType.QUERY_STATISTICS.value,
            data_base64=data_base64,
        )
```

## How to Run the Generator

### General Syntax

```bash
python generator.py --type <event_type> --input <input_json> --output <output_python>
```

**Arguments:**
- `--type`: Event type to generate (`operational`, `audit`, or `statistics`)
- `--input`: Path to input JSON file with event definitions
- `--output`: Path for the generated Python code file

### Example Commands

**Generate Operational Events:**
```bash
python generator.py --type operational \
    --input ../src/analytics_contracts/events/operational_events.json \
    --output ../src/analytics_contracts/events/operational_events_codegen.py
```

**Generate Audit Records:**
```bash
python generator.py --type audit \
    --input ../src/analytics_contracts/audit/audit_records.json \
    --output ../src/analytics_contracts/audit/audit_records_codegen.py
```

**Generate Statistics Events:**
```bash
python generator.py --type statistics \
    --input ../src/analytics_contracts/statistics/statistics_events.json \
    --output ../src/analytics_contracts/statistics/statistics_events_codegen.py
```
