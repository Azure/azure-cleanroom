import logging

import requests
from tenacity import (
    before_sleep_log,
    retry,
    retry_if_exception_type,
    stop_after_attempt,
    wait_fixed,
)

logger = logging.getLogger(__name__)


class HttpClient:
    def __init__(
        self,
        max_retries: int = 12,
        retry_delay: int = 5,
    ):
        self.session = requests.Session()
        self.max_retries = max_retries
        self.retry_delay = retry_delay

    def do_request(
        self,
        method: str,
        endpoint: str,
        data: str = "",
        json: dict = {},
        verify_ssl: bool = True,
        headers: dict = {},
    ) -> None:
        @retry(
            stop=stop_after_attempt(self.max_retries),
            wait=wait_fixed(self.retry_delay),
            retry=retry_if_exception_type(
                (requests.exceptions.ConnectionError, requests.exceptions.Timeout)
            ),
            before_sleep=before_sleep_log(logger, logging.WARNING),
            reraise=True,
        )
        def _execute():
            # Work on a copy of headers to avoid mutating caller state or defaults.
            request_headers = dict(headers or {})
            if json:
                # Only set Content-Type if the caller has not already provided one
                # (case-insensitive check to avoid duplicates with different casing).
                if not any(
                    key.lower() == "content-type" for key in request_headers.keys()
                ):
                    request_headers["Content-Type"] = "application/json"
            response = self.session.request(
                method=method,
                url=endpoint,
                data=data,
                json=json,
                headers=request_headers,
                verify=verify_ssl,
            )
            response.raise_for_status()

        try:
            _execute()
        except (
            requests.exceptions.ConnectionError,
            requests.exceptions.Timeout,
        ) as e:
            logger.error(
                f"Service at {endpoint} is not available after {self.max_retries} attempts . Error: {e}"
            )
            raise

    def post_with_retry(
        self,
        endpoint: str,
        data: str = "",
        json: dict = {},
        headers: dict = {},
        verify_ssl: bool = True,
    ) -> None:
        self.do_request(
            "POST",
            endpoint,
            data=data,
            json=json,
            headers=headers,
            verify_ssl=verify_ssl,
        )

    def put_with_retry(
        self,
        endpoint: str,
        data: str = "",
        verify_ssl: bool = True,
        json: dict = {},
        headers: dict = {},
    ) -> None:
        self.do_request(
            "PUT",
            endpoint,
            data=data,
            json=json,
            headers=headers,
            verify_ssl=verify_ssl,
        )
