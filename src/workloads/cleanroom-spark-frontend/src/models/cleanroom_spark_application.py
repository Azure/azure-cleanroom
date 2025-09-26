from .spark_application_models import SparkApplicationSpec


class Policy:
    def __init__(self, rego: str, rego_base64: str, host_data: str):
        self.rego = rego
        self.rego_base64 = rego_base64
        self.host_data = host_data


class CleanRoomSparkApplication:
    def __init__(
        self, spec: SparkApplicationSpec, driver_policy: Policy, executor_policy: Policy
    ):
        self.spec = spec
        self.driver_policy = driver_policy
        self.executor_policy = executor_policy
