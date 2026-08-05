# Integration tests for k8s-node components.
#
# These tests require a running cluster (Kind or AKS) with both proxies deployed.
# Use `make test-integration-kind` or `make test-integration-aks` to run tests.

REPO_ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST)))/../..)
GO := go
GO_PKG := github.com/azure/azure-cleanroom/src/k8s-node/test/integration

.PHONY: test-integration-kind test-integration-aks help

## test-integration-kind: Run all integration tests against a Kind cluster
test-integration-kind:
	cd $(REPO_ROOT) && $(GO) test -tags "kind,integration,kubelet_proxy,api_server_proxy" -count=1 -v -timeout 30m $(GO_PKG)

## test-integration-aks: Run all integration tests against an AKS flex node
test-integration-aks:
	cd $(REPO_ROOT) && $(GO) test -tags "aks,integration,kubelet_proxy,api_server_proxy" -v -timeout 30m $(GO_PKG)

## help: Show this help message
help:
	@echo "Usage: make [target]"
	@echo ""
	@echo "Targets:"
	@sed -n 's/^##//p' $(MAKEFILE_LIST) | column -t -s ':' | sed -e 's/^/ /'
