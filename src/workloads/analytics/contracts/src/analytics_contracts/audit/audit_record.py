from typing import Dict

from pydantic import BaseModel, Field


class AuditRecord(BaseModel):
    """Represents an audit record."""

    id: int = Field(..., description="Unique identifier for the record")
    source: str = Field(..., description="Source of the audit record")
    name: str = Field(..., description="Name of the record")
    message: str = Field(..., description="Descriptive message for the record")
    parameters: Dict[str, str] = Field(
        default_factory=dict, description="Additional parameters for the record"
    )

    def get_message(self) -> str:
        formatted_message = self.message.format(**self.parameters)
        return f"Event_{self.name}_{self.id} | {formatted_message}"
