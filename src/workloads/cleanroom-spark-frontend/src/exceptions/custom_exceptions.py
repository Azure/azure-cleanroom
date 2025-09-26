from enum import IntEnum


class ErrorCode(IntEnum):
    ResourceNotFound = 1001


class KubernetesClientException(Exception):
    def __init__(self, error_code, *args: object) -> None:
        super().__init__(*args)
        self.error_code = error_code


class ResourceNotFound(KubernetesClientException):
    def __init__(self, *args: object) -> None:
        super().__init__(ErrorCode.ResourceNotFound.value, *args)
