# Project variables
PROJECT_NAME := kubelet-proxy
GO := go
GOFLAGS := -v
MAKEFILE_DIR := $(dir $(abspath $(lastword $(MAKEFILE_LIST))))
REPO_ROOT := $(abspath $(MAKEFILE_DIR)/../../..)
BUILD_DIR := $(MAKEFILE_DIR)/bin
DIST_DIR := $(MAKEFILE_DIR)/dist
GO_PKG_PREFIX := github.com/azure/azure-cleanroom/src/k8s-node/kubelet-proxy
VERSION ?= $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")

# Binary names
KUBELET_PROXY := kubelet-proxy

# Build flags
LDFLAGS := -ldflags "-s -w -X main.version=$(VERSION)"

.PHONY: all build clean kubelet-proxy help release test fmt vet lint deploy-kind teardown-kind test-kind deploy-aks test-aks

## all: Build all binaries
all: build

## build: Build all binaries
build: kubelet-proxy

## kubelet-proxy: Build the kubelet-proxy binary
kubelet-proxy:
	@echo "Building kubelet-proxy..."
	@mkdir -p $(BUILD_DIR)
	cd $(REPO_ROOT) && $(GO) build $(GOFLAGS) $(LDFLAGS) -o $(BUILD_DIR)/$(KUBELET_PROXY) $(GO_PKG_PREFIX)/cmd/kubelet-proxy

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
	
	@# Build kubelet-proxy linux/amd64
	@echo "Building kubelet-proxy linux/amd64..."
	cd $(REPO_ROOT) && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 $(GO) build $(LDFLAGS) -o $(DIST_DIR)/kubelet-proxy $(GO_PKG_PREFIX)/cmd/kubelet-proxy
	tar -czf $(DIST_DIR)/kubelet-proxy-linux-amd64.tar.gz -C $(DIST_DIR) kubelet-proxy
	sha256sum $(DIST_DIR)/kubelet-proxy-linux-amd64.tar.gz | cut -d' ' -f1 > $(DIST_DIR)/kubelet-proxy-linux-amd64.tar.gz.sha256
	@rm $(DIST_DIR)/kubelet-proxy
	
	@echo "Release artifacts created in $(DIST_DIR)/"
	@ls -la $(DIST_DIR)/

## test-kind: Run kubelet-proxy integration tests against the kind cluster
test-kind:
	cd $(REPO_ROOT) && $(GO) test -tags "kind,integration,kubelet_proxy" -v -timeout 30m $(GO_PKG_PREFIX)/../test/integration

## test-aks: Run kubelet-proxy integration tests against the AKS cluster
test-aks:
	cd $(REPO_ROOT) && $(GO) test -tags "aks,integration,kubelet_proxy" -v -timeout 30m $(GO_PKG_PREFIX)/../test/integration

## help: Show this help message
help:
	@echo "Usage: make [target]"
	@echo ""
	@echo "Targets:"
	@sed -n 's/^##//p' $(MAKEFILE_LIST) | column -t -s ':' | sed -e 's/^/ /'
