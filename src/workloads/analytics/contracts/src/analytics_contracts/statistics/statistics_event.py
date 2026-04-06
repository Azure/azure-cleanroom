from pydantic import BaseModel, Field


class StatisticsEvent(BaseModel):
    """Represents a statistics event."""

    type: str = Field(..., description="Type of the statistics event")
    data_base64: str = Field(
        ..., description="Data associated with the statistics event"
    )
