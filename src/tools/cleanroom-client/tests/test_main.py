import http
import os
import urllib
from urllib.request import Request
import json
import urllib.request
import urllib.error
import concurrent.futures
from datetime import datetime

CLEANROOM_CLIENT_BASE_URL = "http://localhost:8080"


def test_deployment_generate():

    request = Request(url=f"{CLEANROOM_CLIENT_BASE_URL}/account/show", method="GET")
    response = _get_json_response(request, http.HTTPStatus.BAD_REQUEST)
    assert response is not None
    assert response["detail"] == "Please run 'az login' to setup account."

    path = os.path.join(os.path.dirname(__file__), "data/samplespec.yaml")
    with open(path, "r") as f:
        spec = f.read()

    request_body = {
        "spec": spec,
        "contract_id": "doesnotmatter",
        "ccf_endpoint": "http://localhost:8000",
        "ssl_server_cert_base64": "doesnotmatter",
        "debug_mode": True,
    }

    request = Request(
        url=f"{CLEANROOM_CLIENT_BASE_URL}/deployment/generate",
        method="POST",
        data=json.dumps(request_body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )

    response = _get_json_response(request)

    assert response is not None
    assert response["duration"] is not None
    assert response["arm_template"] is not None
    assert response["policy_json"] is not None


def test_parallel_deployment_generate():
    num_requests = 10
    path = os.path.join(os.path.dirname(__file__), "data/samplespec.yaml")
    with open(path, "r") as f:
        spec = f.read()

    request_body = {
        "spec": spec,
        "contract_id": "doesnotmatter",
        "ccf_endpoint": "http://localhost:8000",
        "ssl_server_cert_base64": "doesnotmatter",
        "debug_mode": True,
    }

    request = Request(
        url=f"{CLEANROOM_CLIENT_BASE_URL}/deployment/generate",
        method="POST",
        data=json.dumps(request_body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    results = []

    requests = [request] * num_requests
    sum_individual_seconds = 0.0

    start_time = datetime.now()
    with concurrent.futures.ThreadPoolExecutor(max_workers=num_requests) as executor:
        # Submit requests in parallel
        future_to_url = {executor.submit(_get_json_response, r): r for r in requests}

        for future in concurrent.futures.as_completed(future_to_url):
            results.append(future.result())
            sum_individual_seconds += future.result()["duration"]
    end_time = datetime.now()

    assert len(results) == num_requests

    # The test time should be less than the sum of individual request times.
    assert (end_time - start_time).total_seconds() < sum_individual_seconds


def _get_json_response(request: Request, expected_status: int = http.HTTPStatus.OK):
    response = None
    start_time = datetime.now()
    try:
        response = urllib.request.urlopen(request)
        end_time = datetime.now()
        assert response.status == expected_status
        data = response.read()
        encoding = response.info().get_content_charset("utf-8")
        r = json.loads(data.decode(encoding))
        r["duration"] = (end_time - start_time).total_seconds()
        return r
    except urllib.error.HTTPError as e:
        end_time = datetime.now()
        assert e.code == expected_status
        r = json.loads(e.read().decode("utf-8"))
        r["duration"] = (end_time - start_time).total_seconds()
        return r


if __name__ == "__main__":
    test_parallel_deployment_generate()
