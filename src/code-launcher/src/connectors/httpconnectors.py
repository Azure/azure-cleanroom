import logging
import os

import requests
from opentelemetry import trace
from src.utilities import utilities


class HttpConnector:
    @staticmethod
    def httpget(url, query_params, headers):
        resp = requests.get(url=url, headers=headers, params=query_params)
        resp.raise_for_status()
        return resp.json()

    @staticmethod
    def httppost(
        url, query_params=None, post_params=None, json_data=None, headers=None
    ):
        resp = requests.post(
            url=url,
            params=query_params,
            data=post_params,
            json=json_data,
            headers=headers,
        )
        resp.raise_for_status()
        return resp.json()

    @staticmethod
    def httpput(url, query_params=None, data=None, json_data=None, headers=None):
        resp = requests.put(
            url=url,
            params=query_params,
            data=data,
            json=json_data,
            headers=headers,
        )
        resp.raise_for_status()
        return resp


class IdentityHttpConnector:
    port: int = 8290

    @staticmethod
    def set_port(port):
        IdentityHttpConnector.port = port

    @staticmethod
    def fetch_aad_token(tenant_id, client_id, scope):
        logger = logging.getLogger("httpconnectors")
        tracer = trace.get_tracer("httpconnectors")
        with tracer.start_as_current_span(f"fetch_aad_token") as span:
            try:
                url = f"http://localhost:{IdentityHttpConnector.port}/metadata/identity/oauth2/token"
                span.set_attribute("url", url)
                query_params = {
                    "scope": scope,
                    "tenantId": tenant_id,
                    "clientId": client_id,
                    "apiVersion": "2018-02-01",
                }
                headers = {"Content-Type": "application/json"}
                resp = HttpConnector.httpget(url, query_params, headers)
                token = resp.get("token")
                if token is None:
                    raise RuntimeError("identity token not found in response")
                return token
            except requests.HTTPError as e:
                logger.exception(
                    "Fetching Identity token failed with exception", exc_info=True
                )
                span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Fetching Identity token failed",
                )
                span.record_exception(e)


class ACROAuthHttpConnector:
    @staticmethod
    def fetch_acr_refresh_token(private_acr_fqdn, tenant_id, aad_token):
        logger = logging.getLogger("httpconnectors")
        tracer = trace.get_tracer("httpconnectors")
        with tracer.start_as_current_span(f"fetch_acr_refresh_token") as span:
            try:
                url = f"https://{private_acr_fqdn}/oauth2/exchange"
                span.set_attribute("url", url)
                post_params = f"grant_type=access_token&service={private_acr_fqdn}&tenant={tenant_id}&access_token={aad_token}"
                headers = {"Content-Type": "application/x-www-form-urlencoded"}
                resp = HttpConnector.httppost(
                    url=url, post_params=post_params, headers=headers
                )
                if resp.get("refresh_token") is None:
                    raise RuntimeError("ACR refresh_token not found in response")
                return resp.get("refresh_token")
            except requests.HTTPError as e:
                logger.exception(
                    "Fetching ACR token failed with exception", exc_info=True
                )
                span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Fetching ACR token failed",
                )
                span.record_exception(e)


class GovernanceHttpConnector:
    port: int = 8300

    @staticmethod
    def set_port(port):
        GovernanceHttpConnector.port = port

    @staticmethod
    async def put_event(message):
        logger = logging.getLogger("httpconnectors")
        tracer = trace.get_tracer("httpconnectors")
        if not utilities.events_enabled():
            return
        with tracer.start_as_current_span(f"governance_put_event") as span:
            try:
                url = f"http://localhost:{GovernanceHttpConnector.port}/events"
                span.set_attribute("url", url)
                data = {"source": "code-launcher", "message": message}
                headers = {"Content-Type": "application/json"}
                HttpConnector.httpput(url, json_data=data, headers=headers)
            except requests.HTTPError as e:
                logger.exception(
                    "Governance put event failed with exception", exc_info=True
                )
                span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Governance put event failed",
                )
                span.record_exception(e)

    @staticmethod
    async def check_consent(scope: str):
        scope = scope.lower()
        logger = logging.getLogger("httpconnectors")
        tracer = trace.get_tracer("httpconnectors")
        with tracer.start_as_current_span(f"governance_check_{scope}_consent") as span:
            try:
                url = f"http://localhost:{GovernanceHttpConnector.port}/consentcheck/{scope}"
                span.set_attribute("url", url)
                headers = {"Content-Type": "application/json"}
                resp = HttpConnector.httppost(url, headers=headers)
                status = resp["status"]
                if status != "enabled":
                    from ..exceptions.custom_exceptions import ConsentCheckFailure

                    raise ConsentCheckFailure(
                        f"Consent status for {scope} is not enabled. Status is: '{status}'."
                    )
            except Exception as e:
                logger.exception(
                    f"Governance check for {scope} consent failed with exception",
                    exc_info=True,
                )
                span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Governance check for {scope} consent failed",
                )
                span.record_exception(e)
                raise
