#!/usr/bin/env python3

import unittest
import unittest.mock as mock

from ..connectors.httpconnectors import (
    ACROAuthHttpConnector,
    GovernanceHttpConnector,
    HttpConnector,
    IdentityHttpConnector,
    SkrHttpConnector,
)
from ..exceptions.custom_exceptions import ServiceNotAvailableFailure


# TODO (HPrabh): Are these tests required ?
class TestHttpConnector(unittest.TestCase):
    def test_wait_for_it_success(self):
        HttpConnector.wait_for_it("microsoft.com", 443)

    def test_wait_for_it_fail(self):
        with self.assertRaises(ServiceNotAvailableFailure):
            HttpConnector.wait_for_it("test.test", 443, timeout=10, interval=5)

    @mock.patch("src.connectors.httpconnectors.socket")
    @mock.patch("src.connectors.httpconnectors.requests")
    def test_IdentityHttpConnector(self, mock_requests, mock_socket):
        # mock the requests object
        mock_requests.get.return_value = mock.MagicMock(
            status_code=200,
            raise_for_status=mock.MagicMock(return_value=None),
            json=mock.MagicMock(return_value={"token": "<identity_token>"}),
        )

        # mock socket success calls
        mock_socket.socket.return_value = mock.MagicMock()
        identity_token = IdentityHttpConnector.fetch_aad_token(
            "tenant_id", "client_id", 8290, "https://mgmtscope"
        )
        self.assertEqual(identity_token, "<identity_token>")

    @mock.patch("src.connectors.httpconnectors.socket")
    @mock.patch("src.connectors.httpconnectors.requests")
    def test_ACROAuthHttpConnector(self, mock_requests, mock_socket):
        # requests mock
        mock_requests.post.return_value = mock.MagicMock(
            status_code=200,
            raise_for_status=mock.MagicMock(return_value=None),
            json=mock.MagicMock(return_value={"refresh_token": "<refresh_token>"}),
        )

        # mock socket success calls
        mock_socket.socket.return_value = mock.MagicMock()

        acroauth_token = ACROAuthHttpConnector.fetch_acr_refresh_token(
            "private_acr_fqdn", "tenant_id", "aad_token_access_token_mgmt_scope"
        )

        self.assertEqual(acroauth_token, "<refresh_token>")

    @mock.patch("src.connectors.httpconnectors.socket")
    @mock.patch("src.connectors.httpconnectors.requests")
    def test_SKRKeyReleaseHttpConnector(self, mock_requests, mock_socket):
        mock_requests.post.return_value = mock.MagicMock(
            status_code=200,
            raise_for_status=mock.MagicMock(return_value=None),
            json=mock.MagicMock(return_value={"key": "<jwk-pri-key>"}),
        )
        mock_socket.socket.return_value = mock.MagicMock()

        skr_key = SkrHttpConnector.get_released_jwt_key(
            "akv_endpoint", "maa_endpoint", "kid", 8284, "aad_identity_token_mhsm_scope"
        )
        self.assertEqual(skr_key, "<jwk-pri-key>")

    @mock.patch("src.connectors.httpconnectors.socket")
    @mock.patch("src.connectors.httpconnectors.requests")
    def test_GovernanceHttpConnector(self, mock_requests, mock_socket):
        mock_requests.put.return_value = mock.MagicMock(
            status_code=200, raise_for_status=mock.MagicMock(return_value=None)
        )
        mock_socket.socket.return_value = mock.MagicMock()

        GovernanceHttpConnector.put_event("test event", 8300)


if __name__ == "__main__":
    unittest.main()
