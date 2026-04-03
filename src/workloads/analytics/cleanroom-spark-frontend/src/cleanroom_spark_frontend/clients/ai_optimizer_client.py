# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

import json
import logging
from typing import Optional

import requests
from pydantic import BaseModel, Field

logger = logging.getLogger("ai_optimizer_client")


class OptimizedResourceConfig(BaseModel):
    """Optimized resource configuration from AI model"""

    driver_cores: float = Field(alias="driverCores")
    driver_memory: str = Field(alias="driverMemory")
    executor_cores: float = Field(alias="executorCores")
    executor_memory: str = Field(alias="executorMemory")
    executor_instances_min: int = Field(alias="executorInstancesMin")
    executor_instances_max: int = Field(alias="executorInstancesMax")
    reasoning: Optional[str] = None


class AIOptimizerClient:
    """Client for calling the AI optimizer endpoint to get optimized Spark configurations"""

    def __init__(self, endpoint: str, timeout: int = 30):
        self.endpoint = endpoint
        self.timeout = timeout

    def get_optimized_config(
        self, query: str, dataset_info: str
    ) -> Optional[OptimizedResourceConfig]:
        """
        Call the AI optimizer endpoint to get optimized Spark driver/executor configurations.

        Args:
            query: The SQL query to be executed
            dataset_info: Information about the datasets being processed

        Returns:
            OptimizedResourceConfig if successful, None otherwise
        """
        try:
            prompt = self._build_prompt(query, dataset_info)
            response = self._call_ai_model(prompt)

            if response:
                return self._parse_response(response)
            return None

        except Exception as e:
            logger.error(f"Failed to get optimized config from AI: {e}")
            return None

    def _build_prompt(self, query: str, dataset_info: str) -> str:
        """Build the prompt for the AI model"""
        return f"""You are an Apache Spark resource optimization expert. Given a SQL query and dataset information, recommend optimal Spark driver and executor configurations.

SQL Query:
{query}

Dataset Info:
{dataset_info}

Provide recommendations for:
1. Driver cores (float, e.g., 1.0, 2.0)
2. Driver memory (string with unit, e.g., "1g", "2g", "4g")
3. Executor cores (float, e.g., 1.0, 2.0, 4.0)
4. Executor memory (string with unit, e.g., "1g", "2g", "4g")
5. Minimum executor instances (integer)
6. Maximum executor instances (integer)

Respond ONLY with a valid JSON object in this exact format:
{{
    "driverCores": <float>,
    "driverMemory": "<memory>",
    "executorCores": <float>,
    "executorMemory": "<memory>",
    "executorInstancesMin": <int>,
    "executorInstancesMax": <int>,
    "reasoning": "<brief explanation>"
}}

Example: {{"driverCores": 2.0, "driverMemory": "2g", "executorCores": 2.0, "executorMemory": "2g", "executorInstancesMin": 2, "executorInstancesMax": 4, "reasoning": "Medium query complexity with aggregations"}}"""

    def _call_ai_model(self, prompt: str) -> Optional[str]:
        """Call the AI model endpoint"""
        try:
            # Kaito/AIKit llama3.1 endpoint using OpenAI-compatible API
            payload = {
                "model": "llama-3.1-8b-instruct",
                "messages": [{"role": "user", "content": prompt}],
            }

            logger.info(
                f"Calling AI optimizer at {self.endpoint}/v1/chat/completions with payload: {json.dumps(payload)}"
            )
            response = requests.post(
                f"{self.endpoint}/v1/chat/completions",
                json=payload,
                headers={"Content-Type": "application/json"},
                timeout=self.timeout,
            )

            if response.status_code == 200:
                result = response.json()
                # Extract the assistant's message content
                if (
                    "choices" in result
                    and len(result["choices"]) > 0
                    and "message" in result["choices"][0]
                ):
                    content = result["choices"][0]["message"].get("content", "")
                    logger.info(f"AI optimizer response: {content}")
                    return content
                else:
                    logger.error(f"Unexpected AI response format: {result}")
                    return None
            else:
                logger.error(
                    f"AI optimizer returned status {response.status_code}: {response.text}"
                )
                return None

        except requests.exceptions.Timeout:
            logger.error(f"AI optimizer request timed out after {self.timeout}s")
            return None
        except Exception as e:
            logger.error(f"Error calling AI optimizer: {e}")
            return None

    def _parse_response(self, response: str) -> Optional[OptimizedResourceConfig]:
        """Parse the AI model response into configuration"""
        try:
            # Extract JSON from markdown code blocks if present
            if "```json" in response:
                start = response.find("```json") + 7
                end = response.find("```", start)
                response = response[start:end].strip()
            elif "```" in response:
                start = response.find("```") + 3
                end = response.find("```", start)
                response = response[start:end].strip()

            # Try to find JSON object in the response
            start_idx = response.find("{")
            end_idx = response.rfind("}") + 1

            if start_idx >= 0 and end_idx > start_idx:
                json_str = response[start_idx:end_idx]
                config_dict = json.loads(json_str)
                config = OptimizedResourceConfig(**config_dict)
                logger.info(
                    f"Successfully parsed AI config: driver={config.driver_cores}c/{config.driver_memory}, "
                    f"executor={config.executor_cores}c/{config.executor_memory} "
                    f"(instances: {config.executor_instances_min}-{config.executor_instances_max})"
                )
                if config.reasoning:
                    logger.info(f"AI reasoning: {config.reasoning}")
                return config
            else:
                logger.error(f"No valid JSON found in AI response: {response}")
                return None

        except json.JSONDecodeError as e:
            logger.error(
                f"Failed to parse JSON from AI response: {e}, response: {response}"
            )
            return None
        except Exception as e:
            logger.error(f"Error parsing AI optimizer response: {e}")
            return None
