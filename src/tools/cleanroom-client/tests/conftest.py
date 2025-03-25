import docker
import docker.errors
import pytest
import time
import os


def pytest_sessionstart(session):
    """
    Initialize the test session: https://docs.pytest.org/en/6.2.x/reference.html#pytest.hookspec.pytest_sessionstart
    """
    versions_doc_override = os.getenv("AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL")
    registry_override = os.getenv("AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL")

    env_vars = {
        key: value
        for key, value in {
            "AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL": versions_doc_override,
            "AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL": registry_override,
        }.items()
        if value is not None
    }

    client = docker.from_env()
    try:
        container = client.containers.get("cleanroom-client")
        print("Cleaning up existing container")
        if container:
            container.stop()
            container.remove()
    except docker.errors.NotFound:
        pass

    try:
        client.containers.run(
            "cleanroom-client:latest",
            detach=True,
            name="cleanroom-client",
            ports={"80/tcp": 8080},
            environment=env_vars if env_vars else None,
        )

        # Wait for the container to be ready
        time.sleep(5)

    except docker.errors.DockerException as e:
        pytest.exit(f"Failed to start Docker container: {str(e)}")


def pytest_sessionfinish(session, exitstatus):
    """
    Tear down the test session: https://docs.pytest.org/en/6.2.x/reference.html#pytest.hookspec.pytest_sessionfinish
    """
    if exitstatus != 0:  # Non-zero means tests failed or were interrupted
        print(
            "Tests failed or were interrupted. Keeping the container running for debugging."
        )
        return
    client = docker.from_env()
    try:
        container = client.containers.get("cleanroom-client")
        container.stop()
        container.remove()
    except docker.errors.NotFound:
        pass
    except docker.errors.DockerException as e:
        print(f"Error while stopping/removing container: {str(e)}")
