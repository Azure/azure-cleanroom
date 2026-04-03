# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
from dataclasses import dataclass, field
from typing import List, Optional

from kubernetes.client import models as k8smodels


class Sidecar:
    def __init__(self, container: k8smodels.V1Container, virtual_node_policy: str):
        self.container = container
        self.virtual_node_policy = virtual_node_policy


@dataclass
class CleanroomApplication:
    sidecars: List[Sidecar] = field(default_factory=list)
