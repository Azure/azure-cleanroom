# Project variables
PROJECT_NAME := api-server-proxy
GO := go
GOFLAGS := -v
MAKEFILE_DIR := $(dir $(abspath $(lastword $(MAKEFILE_LIST))))
REPO_ROOT := $(abspath $(MAKEFILE_DIR)/../../..)
BUILD_DIR := $(MAKEFILE_DIR)/bin
DIST_DIR := $(MAKEFILE_DIR)/dist
GO_PKG_PREFIX := github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy
VERSION ?= $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")

# Binary names
API_SERVER_PROXY := api-server-proxy

# Build flags
LDFLAGS := -ldflags "-s -w -X main.version=$(VERSION)"

.PHONY: all build clean api-server-proxy help release

## all: Build all binaries
all: build

## build: Build all binaries
build: api-server-proxy

## api-server-proxy: Build the api-server-proxy binary
api-server-proxy:
	@echo "Building api-server-proxy..."
	@mkdir -p $(BUILD_DIR)
	cd $(REPO_ROOT) && $(GO) build $(GOFLAGS) $(LDFLAGS) -o $(BUILD_DIR)/$(API_SERVER_PROXY) $(GO_PKG_PREFIX)/cmd/api-server-proxy

## clean: Remove build artifacts
clean:
	@echo "Cleaning..."
	@rm -rf $(BUILD_DIR) $(DIST_DIR)

## test: Run tests
test:
	cd $(REPO_ROOT) && $(GO) test -v $(GO_PKG_PREFIX)/...

## fmt: Format Go code
fmt:
	cd $(REPO_ROOT) && $(GO) fmt $(GO_PKG_PREFIX)/...

## vet: Run go vet
vet:
	cd $(REPO_ROOT) && $(GO) vet $(GO_PKG_PREFIX)/...

## lint: Run linter (requires golangci-lint)
lint:
	cd $(REPO_ROOT) && golangci-lint run $(GO_PKG_PREFIX)/...

## release: Build release artifacts for all platforms
release: clean
	@echo "Building release artifacts for version $(VERSION)..."
	@mkdir -p $(DIST_DIR)
	
	@# Build api-server-proxy linux/amd64
	@echo "Building api-server-proxy linux/amd64..."
	cd $(REPO_ROOT) && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 $(GO) build $(LDFLAGS) -o $(DIST_DIR)/api-server-proxy $(GO_PKG_PREFIX)/cmd/api-server-proxy
	tar -czf $(DIST_DIR)/api-server-proxy-linux-amd64.tar.gz -C $(DIST_DIR) api-server-proxy
	sha256sum $(DIST_DIR)/api-server-proxy-linux-amd64.tar.gz | cut -d' ' -f1 > $(DIST_DIR)/api-server-proxy-linux-amd64.tar.gz.sha256
	@rm $(DIST_DIR)/api-server-proxy
	
	@# Build api-server-proxy linux/arm64
	@echo "Building api-server-proxy linux/arm64..."
	cd $(REPO_ROOT) && CGO_ENABLED=0 GOOS=linux GOARCH=arm64 $(GO) build $(LDFLAGS) -o $(DIST_DIR)/api-server-proxy $(GO_PKG_PREFIX)/cmd/api-server-proxy
	tar -czf $(DIST_DIR)/api-server-proxy-linux-arm64.tar.gz -C $(DIST_DIR) api-server-proxy
	sha256sum $(DIST_DIR)/api-server-proxy-linux-arm64.tar.gz | cut -d' ' -f1 > $(DIST_DIR)/api-server-proxy-linux-arm64.tar.gz.sha256
	@rm $(DIST_DIR)/api-server-proxy
	
	@echo "Release artifacts created in $(DIST_DIR)/"
	@ls -la $(DIST_DIR)/

## deploy-kind: Deploy api-server-proxy to a kind cluster for testing
deploy-kind:
	@./scripts/kind/deploy-kind.sh

## teardown-kind: Remove the kind test cluster
teardown-kind:
	@./scripts/kind/teardown-kind.sh

## test-kind: Run pod policy verification tests against the kind cluster
test-kind: test-pod-policies

## test-pod-policies: Run pod policy verification tests against the kind cluster
test-pod-policies:
	@./scripts/kind/test-pod-policies.sh

## deploy-aks: Deploy AKS cluster with flex node and api-server-proxy
deploy-aks:
	@./scripts/aks/deploy-cluster.sh
	@./scripts/aks/deploy-flex-node-vm.sh
	@./scripts/aks/deploy-api-server-proxy.sh

## test-aks: Run pod policy verification tests against the AKS cluster
test-aks:
	@./scripts/aks/test-pod-policies.sh

## help: Show this help message
help:
	@echo "Usage: make [target]"
	@echo ""
	@echo "Targets:"
	@sed -n 's/^##//p' $(MAKEFILE_LIST) | column -t -s ':' | sed -e 's/^/ /'
