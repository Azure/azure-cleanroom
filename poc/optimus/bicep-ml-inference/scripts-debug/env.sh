#!/bin/bash
export RG=ml-inference-platform-gsinha
export AKS_NAME=aks-ml-cluster
export NAMESPACE=envoy-gateway-system
export CHART_NAME=gateway-helm
export RELEASE_NAME=gateway-helm
export CHART_VERSION=1.5.3
export REPO_URL=oci://docker.io/envoyproxy
export HELM_ARGS="--create-namespace"
export INITIAL_DELAY=1s