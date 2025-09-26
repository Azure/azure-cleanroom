from typing import Dict, List, Optional

from pydantic import BaseModel, Field

from .model import DatasetFormat


class DatasetInfo(BaseModel):
    name: str
    view_name: str = Field(alias="viewName")
    path: str
    format: DatasetFormat
    schema_: Optional[Dict[str, Dict[str, str]]] = Field(None, alias="schema")


class Configuration(BaseModel):
    query: str = Field(alias="query")
    datasets: List[DatasetInfo] = Field(alias="datasets")
    datasink: DatasetInfo = Field(alias="datasink")
