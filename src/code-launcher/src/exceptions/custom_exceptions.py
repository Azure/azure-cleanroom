from enum import IntEnum


class SystemExitCode(IntEnum):
    ServiceNotAvailableFailureExitCode = 1001
    PrivateACRCmdExecutorFailureExitCode = 1002
    TelemetryCaptureFailure = 1004
    MountPointUnavailableFailure = 1005
    PodmanContainerNotFound = 1006
    PodmanServiceUnreachable = 1007
    ConsentCheckFailure = 1008


class CodeLauncherException(RuntimeError):
    def __init__(self, error_code, *args: object) -> None:
        super().__init__(*args)
        self.error_code = error_code


class ServiceNotAvailableFailure(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.ServiceNotAvailableFailureExitCode.value, *args)


class PrivateACRCmdExecutorFailure(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(
            SystemExitCode.PrivateACRCmdExecutorFailureExitCode.value, *args
        )


class PodmanServiceUnreachable(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.PodmanServiceUnreachable.value, *args)


class PodmanContainerNotFound(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.PodmanContainerNotFound.value, *args)


class PodmanContainerLaunchReturnedNonZeroExitCode(CodeLauncherException):
    def __init__(self, podman_cmd_exit_code, *args: object) -> None:
        super().__init__(podman_cmd_exit_code, *args)


class TelemetryCaptureFailure(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.TelemetryCaptureFailure.value, *args)


class MountPointUnavailableFailure(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.MountPointUnavailableFailure.value, *args)


class ConsentCheckFailure(CodeLauncherException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.ConsentCheckFailure.value, *args)
