import os

from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.exceptions.exception import *
from cleanroom_common.azure_cleanroom_core.models.query import Query, QuerySegment
from cleanroom_common.azure_cleanroom_core.utilities.configuration_helpers import (
    read_querysegment_file_config,
    write_querysegment_file_config,
)

from ..utilities._azcli_helpers import logger
from ._configuration_helpers import get_default_config_file


class QuerySegmentHelper:
    """
    Helper class used to read and write the query segment configuration file / input.
    """

    @staticmethod
    def load(config_file: str, create_if_not_existing: bool = False) -> Query:

        try:
            spec = read_querysegment_file_config(config_file, logger)
        except FileNotFoundError:
            if (not os.path.exists(config_file)) and create_if_not_existing:
                spec = Query(segments=[])
            else:
                raise CLIError(
                    f"Cannot find file {config_file}. Check the --*-config parameter value."
                )

        return spec

    @staticmethod
    def store(config_file: str, config: Query) -> None:
        write_querysegment_file_config(config_file, config, logger)

    @staticmethod
    def generate_segment_from_fields(
        executionsequence: int,
        query_content: str,
        pre_conditions: str,
        post_filters: str,
    ) -> QuerySegment:
        """
        Parses the input strings for adding a query segment and returns a QuerySegment object.
        :param executionsequence: The execution sequence number of the query segment.
        :param query_content: The SQL query content.
        :param pre_conditions: Comma-separated pre-segment conditions in 'viewName:minRowCount' format.
        :param post_filters: Comma-separated post-segment filter
        :return: A QuerySegment object.
        """

        pre_conditions_json = []
        if pre_conditions.strip() == "":
            logger.warning("No pre-segment conditions provided.")
        else:
            # Convert comma-separated viewName:minRowCount pairs to a JSON object
            for cond in pre_conditions.split(","):
                cond = cond.strip()
                if cond:
                    if ":" not in cond:
                        raise CLIError(
                            f"Invalid pre-segment condition format: '{cond}'."
                            + " Expected format 'viewName:minRowCount'."
                        )
                    viewName, value = cond.split(":", 1)
                    pre_conditions_json.append(
                        {
                            "viewName": viewName.strip(),
                            "minRowCount": int(value.strip()),
                        }
                    )

        post_filters_json = []
        if post_filters.strip() == "":
            logger.warning("No post-segment filterings provided.")
        else:
            # Convert comma-separated columnName:value pairs to a JSON object
            for cond in post_filters.split(","):
                cond = cond.strip()
                if cond:
                    if ":" not in cond:
                        raise CLIError(
                            f"Invalid post-segment filtering format: '{cond}'."
                            + " Expected format 'columnName:value'."
                        )
                    columnName, value = cond.split(":", 1)
                    post_filters_json.append(
                        {"columnName": columnName.strip(), "value": int(value.strip())}
                    )

        # Create a new QuerySegment object.
        try:
            return QuerySegment(
                executionSequence=executionsequence,
                data=query_content,
                preConditions=pre_conditions_json,
                postFilters=post_filters_json,
            )

        except Exception as e:
            raise CLIError(f"Error creating QuerySegment: {str(e)}")
