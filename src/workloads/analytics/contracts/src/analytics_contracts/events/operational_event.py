from typing import Dict

from pydantic import BaseModel, Field


class OperationalEvent(BaseModel):
    id: int = Field(..., description="Unique identifier for the event")
    name: str = Field(..., description="Name of the event")
    message: str = Field(..., description="Descriptive message for the event")
    parameters: Dict[str, str] = Field(
        default_factory=dict, description="Additional parameters for the event"
    )

    def get_message(self) -> str:
        formatted_message = self.message.format(**self.parameters)
        return f"Event_{self.id} | {formatted_message}"
