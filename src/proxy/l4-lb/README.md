# Debugging steps for SSL failures

Get shell access to the envoy container and dynamically change log level to debug:
```sh
curl -X POST http://localhost:9901/logging?level=debug
```

Reproduce the failure:
```sh
curl -k https://<envoy-ip>:443/node/network
```

Inspect the container logs.

Reset logging:
```sh
curl -X POST http://localhost:9901/logging?level=info
```

To know current logging level:
```sh
curl http://localhost:9901/logging
```
