import os

from kubernetes.client import models as k8smodels
from src.builders.spark_application_builder import SparkApplicationBuilder
from src.config.configuration import CleanroomSettings, DriverSettings, ExecutorSettings
from src.utilities.constants import Constants

CCE_POLICY_ANNOTATION_VALUE = "microsoft.containerinstance.virtualnode.ccepolicy"


class ConfidentialVN2SparkApplicationBuilder(SparkApplicationBuilder):
    def __init__(self, cleanroom_settings: CleanroomSettings):
        super().__init__(cleanroom_settings)

    def _get_driver(self, settings: DriverSettings):
        driver, policy = super()._get_driver(settings)

        # The kubernetes_master should be set to the FQDN in case of AKS.
        # For that, set the annotation kubernetes.azure.com/set-kube-service-host-fqdn: "true"
        # for the frontend pod.
        # Otherwise, this will be a local IP that the Spark Pod cannot resolve in the C-ACI because
        # of the absence of kube-proxy.
        kubernetes_master = os.environ.get("KUBERNETES_SERVICE_HOST")
        driver.kubernetesMaster = f"https://{kubernetes_master}"

        driver.annotations["microsoft.containerinstance.virtualnode.injectdns"] = (
            "false"
        )
        driver.annotations["kubernetes.azure.com/set-kube-service-host-fqdn"] = "true"
        driver.labels[Constants.CCE_POLICY_INJECTOR_LABEL] = "true"
        driver.labels[Constants.CCE_POLICY_ANNOTATION_NAME_LABEL] = (
            CCE_POLICY_ANNOTATION_VALUE
        )
        driver.serviceLabels = {"service": "external-dns"}

        driver.nodeSelector["virtualization"] = "virtualnode2"
        driver.tolerations = [
            k8smodels.V1Toleration(
                effect="NoSchedule",
                key="virtual-kubelet.io/provider",
                operator="Exists",
            )
        ]

        return (driver, policy)

    def _get_executor(self, settings: ExecutorSettings):
        (executor, policy) = super()._get_executor(settings)
        executor.annotations["microsoft.containerinstance.virtualnode.injectdns"] = (
            "false"
        )
        executor.annotations["kubernetes.azure.com/set-kube-service-host-fqdn"] = "true"
        executor.labels[Constants.CCE_POLICY_INJECTOR_LABEL] = "true"
        executor.labels[Constants.CCE_POLICY_ANNOTATION_NAME_LABEL] = (
            CCE_POLICY_ANNOTATION_VALUE
        )
        executor.nodeSelector["virtualization"] = "virtualnode2"
        executor.tolerations = [
            k8smodels.V1Toleration(
                effect="NoSchedule",
                key="virtual-kubelet.io/provider",
                operator="Exists",
            )
        ]
        return (executor, policy)
