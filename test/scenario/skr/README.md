# Key Release

This scenario tests out the SKR sidecar in isolation. It uses the SKR (Secure Key Release) sidecar from the [Confidential Sidecars Repository](https://github.com/microsoft/confidential-sidecar-containers).

This scenario uses a single statically created mHSM to test Key Release.

The test is performed by mounting a test container alongside the SKR container. When the test container boots up, it makes an HTTP request to the SKR's endpoint and a successful response implies a successful test.

The container spec for this scenario can be found at [container-spec.json](./container-spec.json).