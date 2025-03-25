import logging
import podman
import podman.api
import podman.domain
import podman.domain.containers

from functools import wraps
from typing import Any

from cleanroomspec.models.python.model import *
from ..connectors.httpconnectors import (
    ACROAuthHttpConnector,
    GovernanceHttpConnector,
    IdentityHttpConnector,
)
from ..utilities import utilities
from ..constants import constants

from opentelemetry import trace


def invoke_podman(func):

    @wraps(func)
    async def invoke(*args, **kwargs):
        from podman import PodmanClient
        from podman.errors import PodmanError

        logger = logging.getLogger("podman_utilities")
        tracer = trace.get_tracer("podman_utilities")

        with PodmanClient(base_url="unix:///run/podman/podman.sock") as client:
            kwargs["_podmanClient"] = client
            kwargs["_logger"] = logger
            kwargs["_tracer"] = tracer
            with tracer.start_as_current_span(f"{func.__name__}") as span:
                try:
                    return await func(*args, **kwargs)
                except Exception as e:
                    logger.error(
                        f"Podman invocation failed for {func.__name__} with exception {e}."
                    )
                    span.set_status(
                        status=trace.StatusCode.ERROR,
                        description=f"Podman invocation failed for {func.__name__}.",
                    )
                    span.record_exception(e)
                    raise

    return invoke


@invoke_podman
async def ping(**kwargs) -> bool:
    client = kwargs["_podmanClient"]
    return client.ping()


@invoke_podman
async def fetch_application_image(application: Application, **kwargs):
    from podman.domain.images_manager import ImagesManager
    from podman.errors import ImageNotFound

    client = kwargs["_podmanClient"]
    logger = kwargs["_logger"]
    tracer = kwargs["_tracer"]
    image_url = application.image.executable.backingResource.id

    imagesManager = ImagesManager(client.api)
    with tracer.start_as_current_span(f"podman-image-get") as podman_span:
        try:
            imagesManager.get(image_url)
            return
        except ImageNotFound:
            logger.info(f"Image {image_url} not found locally. Pulling it.")
            pass

    acr_url = application.image.executable.backingResource.provider.url
    identity = application.image.executable.identity

    await GovernanceHttpConnector.put_event(
        f"Pulling image {image_url}",
    )

    auth_config = None
    if identity is not None:
        logger.info(f"Logging in to {acr_url} using {identity}")
        aad_token = IdentityHttpConnector.fetch_aad_token(
            identity.tenantId,
            identity.clientId,
            constants.SCOPE.MGMT_SCOPE,
        )

        acr_ref_token = ACROAuthHttpConnector.fetch_acr_refresh_token(
            acr_url, identity.tenantId, aad_token
        )

        auth_config = {
            "username": "00000000-0000-0000-0000-000000000000",
            "password": acr_ref_token,
        }

    # In case of repositories with the port in the URL, like ccr-registry:5000, the parsing logic within podman code,
    # parses it incorrectly and appends an extra tag ":latest" to the image which causes the error "invalid reference format".
    # However, the call does not fail but the image is not pulled.
    # TODO (HPrabh): Check why the exception is not being propagated out.
    repository, tag = image_url.rsplit(":", 1)
    image = imagesManager.pull(repository=repository, tag=tag, auth_config=auth_config)
    await GovernanceHttpConnector.put_event(
        f"Pulled image {image_url}",
    )
    logger.info(f"Successfully pulled image {image_url}")


@invoke_podman
async def start_application_container(
    application: Application, telemetry_path: str, **kwargs
):
    from podman.domain.images_manager import ImagesManager

    from podman.errors import NotFound, PodmanError

    client = kwargs["_podmanClient"]
    logger = kwargs["_logger"]
    tracer = kwargs["_tracer"]

    if application.datasources:
        for key in application.datasources.keys():
            logger.info(f"waiting for mount point for {key}")
            utilities.wait_for_mount_point(key)

    if application.datasinks:
        for key in application.datasinks.keys():
            logger.info(f"waiting for mount point for {key}")
            utilities.wait_for_mount_point(key)

    image_url = application.image.executable.backingResource.id
    imagesManager = ImagesManager(client.api)
    image = imagesManager.get(image_url)
    mounts: list[dict[str, Any]] = []

    logger.info(f"Creating container for image: {image_url}")
    create_container = False
    with tracer.start_as_current_span(f"podman-container-get") as podman_span:
        try:
            container = client.containers.get(application.name)
            logger.warning(
                f"Container for application {application.name} found. Re-starting container."
            )
        except NotFound:
            create_container = True

    if create_container:
        if application.datasources:
            for key, value in application.datasources.items():
                datasource_mount_path = utilities.get_mount_path(key)
                mounts.append(
                    {
                        "type": "bind",
                        "source": f"{datasource_mount_path}",
                        "target": f"{value}",
                        "read_only": True,
                    }
                )
        if application.datasinks:
            for key, value in application.datasinks.items():
                datasink_mount_path = utilities.get_mount_path(key)
                mounts.append(
                    {
                        "type": "bind",
                        "source": f"{datasink_mount_path}",
                        "target": f"{value}",
                        "read_only": False,
                    }
                )

        logger.info(f"Executing podman with command: {application.command}")

        await GovernanceHttpConnector.put_event(
            f"Creating container for {application.name}",
        )

        with tracer.start_as_current_span(f"podman-container-create") as podman_span:
            try:
                container = client.containers.create(
                    image=image,
                    name=application.name,
                    command=application.command,
                    environment=application.environmentVariables,
                    mounts=mounts,
                    user="1000",
                    log_config={
                        "Type": "json-file",
                        "Config": {
                            "path": f"{telemetry_path}/application/{application.name}.log"
                        },
                    },
                )
            except PodmanError as pe:
                logger.error(f"Create container failed with error {pe}")
                podman_span.set_status(
                    status=trace.StatusCode.ERROR,
                    description=f"Create container failed.",
                )
                podman_span.record_exception(pe)

        await GovernanceHttpConnector.put_event(
            f"Created container with Id {container.id} for {application.name}",
        )

    await GovernanceHttpConnector.put_event(
        f"Starting execution of {application.name} container",
    )

    with tracer.start_as_current_span(f"podman-container-start") as podman_span:
        try:
            container.start()
        except PodmanError as pe:
            logger.error(f"Container start failed with error {pe}")
            podman_span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Container start failed with error",
            )
            podman_span.record_exception(pe)


@invoke_podman
async def get_application_status(application_name: str, **kwargs):
    from podman.errors import NotFound, PodmanError
    from src.exceptions.custom_exceptions import PodmanContainerNotFound

    client = kwargs["_podmanClient"]
    logger = kwargs["_logger"]
    tracer = kwargs["_tracer"]

    with tracer.start_as_current_span(f"podman-container-create") as podman_span:
        try:
            container = client.containers.get(application_name)
        except NotFound as nf:
            podman_span.record_exception(nf)
            raise PodmanContainerNotFound(application_name)
        except PodmanError as pe:
            logger.error(f"Container get failed with error {pe}")
            podman_span.set_status(
                status=trace.StatusCode.ERROR,
                description=f"Container get failed with error",
            )
            podman_span.record_exception(pe)
            raise

    exit_code = 0
    if container.status == "stopped" or container.status == "exited":
        exit_code = container.wait()

    await GovernanceHttpConnector.put_event(
        f"{application_name} container status: {container.status}, exit code: {exit_code}",
    )

    logger.info(
        f"Application Name: {application_name}, status: {container.status}, exit_code: {exit_code}"
    )
    return {"status": container.status, "exit_code": exit_code}
