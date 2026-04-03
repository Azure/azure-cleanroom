from typing import List

from frontend_internal.models.cleanroom_application import Sidecar

from .inference_service_models import InferenceServiceSpec


class Policy:
    def __init__(self, json: str, json_base64: str, pcrs: dict[str, str]):
        self.json = json
        self.json_base64 = json_base64
        self.pcrs = pcrs


class CleanRoomInferencingApplication:
    def __init__(
        self,
        spec: InferenceServiceSpec,
        predictor_policy: Policy,
        transformer_policy: Policy,
        sidecars: List[Sidecar] = [],
    ):
        self.spec = spec
        self.predictor_policy = predictor_policy
        self.transformer_policy = transformer_policy
        self.sidecars = sidecars
