#!/usr/bin/env python3
import unittest
import unittest.mock as mock

from ..code_launcher import main, parse_args

# TODO (HPrabh): Fix the tests.


class TestCodeLauncherMain(unittest.TestCase):
    def test_parse_args_pubic_reg_unenc_image(self):
        # case 1: Public registry and unencrypted image
        parsed_arguments = parse_args(
            [
                "--",
                "--name",
                "curl-nonroot-network-egress",
                "-h",
                "www.microsoft.com",
                "-p",
                "443",
                "-t",
                "120",
            ]
        )
        self.assertEqual(len(parsed_arguments.podmanrunparams), 8)

    def test_parse_args_pri_reg_validation_failure(self):
        # case 2: private registry validation failure
        with self.assertRaises(SystemExit):
            parse_args(
                [
                    "--private-acr-fqdn",
                    "test-reg.azurecr.io/test-image",
                    "--",
                    "--name",
                    "curl-nonroot-network-egress",
                    "-h",
                    "www.microsoft.com",
                ]
            )

    def test_parse_args_enc_img_validation_failure(self):
        # case 3: encrypted image validation failure
        with self.assertRaises(SystemExit):
            parse_args(
                [
                    "--encrypted-image",
                    "test-reg.azurecr.io/test-image",
                    "--",
                    "--name",
                    "curl-nonroot-network-egress",
                    "-h",
                    "www.microsoft.com",
                ]
            )

    def test_parse_args_pri_reg_validation_success(self):
        # case 4: private registry validation success
        parsed_arguments = parse_args(
            [
                "--private-acr-fqdn",
                "test-reg.azurecr.io/test-image",
                "--tenant-id",
                "72f988bf-86f1-41af-91ab-2d7cd011db47",
                "--client-id",
                "eabc0017-db41-4acc-b21b-58e583fa5fda",
                "--",
                "--name",
                "curl-nonroot-network-egress",
                "-h",
                "www.microsoft.com",
                "-p",
                "443",
                "-t",
                "120",
            ]
        )
        self.assertEqual(len(parsed_arguments.podmanrunparams), 8)

    def test_parse_args_enc_img_validation_success(self):
        parsed_arguments = parse_args(
            [
                "--encrypted-image",
                "test-reg.azurecr.io/test-image",
                "--tenant-id",
                "72f988bf-86f1-41af-91ab-2d7cd011db47",
                "--client-id",
                "eabc0017-db41-4acc-b21b-58e583fa5fda",
                "--akv-endpoint",
                "cleanroommhsm.managedhsm.azure.net",
                "--kid",
                "imageKey",
                "--maa-endpoint",
                "maaemdpoint.microsft.com",
                "--",
                "--name",
                "curl-nonroot-network-egress",
                "-h",
                "www.microsoft.com",
                "-p",
                "443",
                "-t",
                "120",
            ]
        )
        self.assertEqual(len(parsed_arguments.podmanrunparams), 8)

    @mock.patch("src.code_launcher.PrivateACRCmdExecutor")
    @mock.patch("src.code_launcher.EncContainerCmdExecutor")
    @mock.patch("src.code_launcher.os")
    @mock.patch("src.code_launcher.run")
    def test_main(
        self,
        mock_run,
        mock_os,
        mock_enc_container_cmd_exec,
        mock_private_container_cmd_exec,
    ):
        # mock objects
        mock_os.environ.get.return_value = None
        mock_os.makedirs.return_value = None
        mock_run.return_value = mock.MagicMock(returncode=0)
        mock_enc_container_cmd_exec.return_value.execute.return_value = None
        mock_private_container_cmd_exec.return_value.execute.return_value = None

        cmd_params = [
            "--encrypted-image",
            "test-reg.azurecr.io/test-image",
            "--tenant-id",
            "72f988bf-86f1-41af-91ab-2d7cd011db47",
            "--client-id",
            "eabc0017-db41-4acc-b21b-58e583fa5fda",
            "--akv-endpoint",
            "cleanroommhsm.managedhsm.azure.net",
            "--kid",
            "imageKey",
            "--maa-endpoint",
            "maaemdpoint.microsft.com",
            "--",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        expected_podman_params = [
            "podman",
            "run",
            "--log-driver=json-file",
            "--log-opt=path=/mnt/telemetry/application/app.log",
            "--user=1000:1000",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        main(cmd_params, "src/logger/logconfig.ini", ".")
        # assert executors called
        self.assertTrue(mock_private_container_cmd_exec.return_value.execute.called)
        self.assertTrue(mock_enc_container_cmd_exec.return_value.execute.called)

        mock_private_container_cmd_exec.return_value.execute.assert_called_once()
        mock_enc_container_cmd_exec.return_value.execute.assert_called_once()

        # assert default params and podman run with right parameters.
        self.assertTrue(mock_os.environ.get.called)
        self.assertTrue(mock_run.called)
        mock_run.assert_called_once_with(expected_podman_params)

    @mock.patch("src.code_launcher.PrivateACRCmdExecutor")
    @mock.patch("src.code_launcher.EncContainerCmdExecutor")
    @mock.patch("src.code_launcher.os")
    @mock.patch("src.code_launcher.run")
    @mock.patch("src.code_launcher.shutil")
    def test_main_export_telemetry(
        self,
        mock_shutil,
        mock_run,
        mock_os,
        mock_enc_container_cmd_exec,
        mock_private_container_cmd_exec,
    ):
        # mock objects
        mock_os.environ.get.return_value = None
        mock_os.makedirs.return_value = None
        mock_shutil.copy.return_value = None
        mock_run.return_value = mock.MagicMock(returncode=0)
        mock_enc_container_cmd_exec.return_value.execute.return_value = None
        mock_private_container_cmd_exec.return_value.execute.return_value = None

        cmd_params = [
            "--encrypted-image",
            "test-reg.azurecr.io/test-image",
            "--tenant-id",
            "72f988bf-86f1-41af-91ab-2d7cd011db47",
            "--client-id",
            "eabc0017-db41-4acc-b21b-58e583fa5fda",
            "--akv-endpoint",
            "cleanroommhsm.managedhsm.azure.net",
            "--kid",
            "imageKey",
            "--maa-endpoint",
            "maaemdpoint.microsft.com",
            "--export_telemetry",
            "/mnt/samplevolmount",
            "--",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        expected_cmd_run = [
            "podman",
            "run",
            "--log-driver=json-file",
            "--log-opt=path=/mnt/telemetry/application/app.log",
            "--user=1000:1000",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        main(cmd_params, "src/logger/logconfig.ini", ".")

        # assert calls
        self.assertTrue(mock_run.called)
        self.assertTrue(mock_shutil.copy.called)

        mock_run.assert_called_once_with(expected_cmd_run)
        mock_shutil.copy.assert_called_once_with(
            "/mnt/telemetry/application/*.*",
            "/mnt/samplevolmount/application-telemetry",
        )

    @mock.patch("src.code_launcher.PrivateACRCmdExecutor")
    @mock.patch("src.code_launcher.EncContainerCmdExecutor")
    @mock.patch("src.code_launcher.os")
    @mock.patch("src.code_launcher.run")
    @mock.patch("src.code_launcher.shutil")
    def test_main_export_telemetry_raise_exp(
        self,
        mock_shutil,
        mock_run,
        mock_os,
        mock_enc_container_cmd_exec,
        mock_private_container_cmd_exec,
    ):
        # mock objects
        mock_os.environ.get.return_value = None
        mock_os.makedirs.return_value = None
        mock_shutil.copy = mock.MagicMock(side_effect=IOError("IOError"))
        mock_run.return_value = mock.MagicMock(returncode=0)
        mock_enc_container_cmd_exec.return_value.execute.return_value = None
        mock_private_container_cmd_exec.return_value.execute.return_value = None

        cmd_params = [
            "--encrypted-image",
            "test-reg.azurecr.io/test-image",
            "--tenant-id",
            "72f988bf-86f1-41af-91ab-2d7cd011db47",
            "--client-id",
            "eabc0017-db41-4acc-b21b-58e583fa5fda",
            "--akv-endpoint",
            "cleanroommhsm.managedhsm.azure.net",
            "--kid",
            "imageKey",
            "--maa-endpoint",
            "maaemdpoint.microsft.com",
            "--export_telemetry",
            "/mnt/samplevolmount",
            "--",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        expected_cmd_run = [
            "podman",
            "run",
            "--log-driver=json-file",
            "--log-opt=path=/mnt/telemetry/application/app.log",
            "--user=1000:1000",
            "--name",
            "curl-nonroot-network-egress",
            "test-reg.azurecr.io/test-image",
            "-h",
            "www.microsoft.com",
            "-p",
            "443",
            "-t",
            "120",
        ]
        main(cmd_params, "src/logger/logconfig.ini", ".")

        # assert calls
        self.assertTrue(mock_run.called)
        self.assertTrue(mock_shutil.copy.called)

        mock_run.assert_called_once_with(expected_cmd_run)
        mock_shutil.copy.assert_called_once_with(
            "/mnt/telemetry/application/*.*",
            "/mnt/samplevolmount/application-telemetry",
        )


if __name__ == "__main__":
    unittest.main()
