# Image for building the executable.
FROM mcr.microsoft.com/oss/go/microsoft/golang:1.25.0 AS build-image

# Install prerequisities.
RUN apt-get update -y && \
    DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends install \
    software-properties-common build-essential

RUN curl -sSfL https://raw.githubusercontent.com/golangci/golangci-lint/master/install.sh | sh -s -- -b $(go env GOPATH)/bin v2.8.0

# Set the working directory
WORKDIR /app

# Download dependencies. If go.mod/sum files are unchanged then layer caching optimization kicks in.
COPY src/go.mod .
COPY src/go.sum .
RUN go mod download

# Copy the source.
COPY src/ src/

# Lint and build.
WORKDIR /app/src
RUN golangci-lint run -v ./...
RUN go build -o /app/main

# Optimize the final image size by creating an image with only the executable.
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04

RUN apt-get -y update && \
    DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends install \
    ca-certificates && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build-image /app/main ./main
RUN chmod +x ./main
