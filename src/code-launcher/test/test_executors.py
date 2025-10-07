#!/usr/bin/env python3

import argparse
import os.path
import unittest
import unittest.mock as mock

from ..cmd_executors.executors import (
    EncContainerCmdExecutor,
    PrivateACRCmdExecutor,
    TempFileHandler,
)

# TODO (HPrabh): Fix the tests.


class TestCmdExecutors(unittest.TestCase):
    def test_tempFileHandler(self):
        tempfilehandler_ = TempFileHandler("temp.txt")
        with tempfilehandler_ as file_:
            file_.write("test".encode())
            file_.flush()
        # assert file is closed and deleted
        self.assertFalse(os.path.isfile("temp.txt"))

    @mock.patch("src.cmd_executors.executors.IdentityHttpConnector")
    @mock.patch("src.cmd_executors.executors.ACROAuthHttpConnector")
    @mock.patch("src.cmd_executors.executors.run")
    def test_PrivateACRCmdExecutor(
        self, mock_run, mock_acr_oauth_connector, mock_identity_connector
    ):
        # mock connector calls
        mock_identity_connector.fetch_aad_token.return_value = "identity_token"
        mock_acr_oauth_connector.fetch_acr_refresh_token.return_value = "ref_token"
        mock_run.return_value = mock.MagicMock(returncode=0)

        args = argparse.Namespace(
            tenant_id="tenant_id",
            client_id="client_id",
            identity_port=8290,
            private_acr_fqdn="private_acr_fqdn",
        )
        PrivateACRCmdExecutor().execute(args)
        self.assertTrue(mock_identity_connector.fetch_aad_token.called)
        self.assertTrue(mock_acr_oauth_connector.fetch_acr_refresh_token.called)
        self.assertTrue(mock_run.called)

        mock_identity_connector.fetch_aad_token.assert_called_once_with(
            "tenant_id", "client_id", 8290, "https://management.azure.com/.default"
        )

        mock_acr_oauth_connector.fetch_acr_refresh_token.assert_called_once_with(
            "private_acr_fqdn", "tenant_id", "identity_token"
        )

        mock_run.assert_called_once_with(
            [
                "podman",
                "login",
                "-u",
                "00000000-0000-0000-0000-000000000000",
                "-p",
                "ref_token",
                "private_acr_fqdn",
                "-v",
            ]
        )

    @mock.patch("src.cmd_executors.executors.IdentityHttpConnector")
    @mock.patch("src.cmd_executors.executors.SkrHttpConnector")
    @mock.patch("src.cmd_executors.executors.run")
    @mock.patch("src.cmd_executors.executors.jwk")
    def test_EncContainerCmdExecutor(
        self,
        mock_jwk,
        mock_run,
        mock_skr_key_release_connector,
        mock_identity_connector,
    ):
        skr_jwk_str = '{"key": "<KEY>"}'
        mock_identity_connector.fetch_aad_token.return_value = "identity_token"
        mock_skr_key_release_connector.get_released_jwt_key.return_value = skr_jwk_str

        jwk_mock_ = mock.MagicMock()
        mock_jwk.JWK.return_value = jwk_mock_
        jwk_mock_.export_to_pem.return_value = "test_pem".encode()
        mock_run.return_value = mock.MagicMock(returncode=0)
        args = argparse.Namespace(
            tenant_id="tenant_id",
            client_id="client_id",
            identity_port=8290,
            skr_port=8283,
            encrypted_image="enc_image",
            akv_endpoint="key_vault",
            maa_endpoint="maa_endpoint",
            kid="kid",
        )
        EncContainerCmdExecutor().execute(args)

        self.assertTrue(mock_identity_connector.fetch_aad_token.called)
        self.assertTrue(mock_skr_key_release_connector.get_released_jwt_key.called)
        self.assertTrue(mock_run.called)

        mock_identity_connector.fetch_aad_token.assert_called_once_with(
            "tenant_id", "client_id", 8290, "https://managedhsm.azure.net/.default"
        )

        mock_skr_key_release_connector.get_released_jwt_key.assert_called_once_with(
            "key_vault", "maa_endpoint", "kid", 8283, "identity_token"
        )

        mock_run.assert_called_once_with(
            ["podman", "pull", "--decryption-key=privkey.pem", "enc_image"]
        )


if __name__ == "__main__":
    unittest.main()
