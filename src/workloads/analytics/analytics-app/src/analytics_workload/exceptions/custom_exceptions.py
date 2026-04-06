from enum import Enum


class SystemExitCode(Enum):
    MountPointUnavailableFailure = 1001


class SparkApplicationException(Exception):
    def __init__(self, error_code, *args: object) -> None:
        super().__init__(*args)
        self.error_code = error_code


class MountPointUnavailableFailure(SparkApplicationException):
    def __init__(self, *args: object) -> None:
        super().__init__(SystemExitCode.MountPointUnavailableFailure.value, *args)
