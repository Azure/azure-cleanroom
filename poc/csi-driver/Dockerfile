FROM cleanroombuild.azurecr.io/mirror/docker/library/golang:1.25 AS go-builder
WORKDIR /build
COPY poc/csi-driver/go.mod poc/csi-driver/go.sum ./
RUN go mod download
COPY poc/csi-driver/ .
RUN CGO_ENABLED=0 GOOS=linux go build -o /csi-driver .
RUN CGO_ENABLED=0 GOOS=linux go build -o /blobfuse-proxy ./proxy/

FROM cleanroombuild.azurecr.io/mirror/docker/library/python:3.12 AS wheel-builder
ARG UV_DEFAULT_INDEX=https://pypi.org/simple
ENV UV_DEFAULT_INDEX=$UV_DEFAULT_INDEX
WORKDIR /build
RUN apt-get update && apt-get install -y curl build-essential && rm -rf /var/lib/apt/lists/*
RUN curl -LsSf https://astral.sh/uv/install.sh | sh
ENV PATH="/root/.local/bin:$PATH"
COPY src/blobfuse-launcher src/blobfuse-launcher
COPY src/internal/cleanroom-internal src/internal/cleanroom-internal
COPY pyproject.toml .
COPY uv.lock .
RUN uv lock && uv sync --frozen
RUN uv build --all-packages --wheel

# Stage 3: Build blobfuse2 from source + encryptor plugin
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04 AS blobfuse-builder

RUN apt-get update -y && \
    DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends install \
    software-properties-common build-essential ca-certificates wget \
    libfuse3-dev gcc

COPY external/azure-storage-fuse azure-storage-fuse
COPY build/go_installer.sh .
RUN chmod +x go_installer.sh
RUN chmod +x azure-storage-fuse/build.sh
RUN ./go_installer.sh .
RUN cd azure-storage-fuse && ./build.sh

# Build encryptor plugin.
COPY src/blobfuse-launcher/encryptor azure-storage-fuse/encryptor
RUN cd azure-storage-fuse/encryptor && go build -buildmode=plugin -o encryptor.so

# Stage 4: Runtime - combine Go binary + Python + custom blobfuse2 + encryptor
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04

ARG PIP_INDEX_URL=https://pypi.org/simple
ENV PIP_INDEX_URL=$PIP_INDEX_URL

WORKDIR /app

# Install blobfuse dependencies + Python + fuse3.
RUN apt-get update -y && \
    apt-get -y --no-install-recommends install \
    software-properties-common fuse3 curl python3.12 ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Copy custom blobfuse2 binary (with plugin support) and encryptor plugin.
COPY --from=blobfuse-builder /azure-storage-fuse/blobfuse2 /usr/local/bin/
COPY --from=blobfuse-builder /azure-storage-fuse/encryptor/encryptor.so /app/encryptor.so
COPY src/blobfuse-launcher/encryptor-config.yaml /app/encryptor-config.yaml
ENV BLOBFUSE_PLUGIN_PATH=/app/encryptor.so
RUN ldconfig /usr/local/lib64/

# Install Python wheels (blobfuse_launcher + cleanroom_internal).
WORKDIR /setup
COPY --from=wheel-builder /build/dist/cleanroom_internal*.whl /setup/
COPY --from=wheel-builder /build/dist/blobfuse_launcher*.whl /setup/
RUN curl -sS https://bootstrap.pypa.io/get-pip.py | python3.12 - --break-system-packages
RUN python3.12 -m pip install --break-system-packages *.whl && rm -f *.whl

# Copy Go CSI driver and blobfuse-proxy binaries.
WORKDIR /app
COPY --from=go-builder /csi-driver /app/csi-driver
COPY --from=go-builder /blobfuse-proxy /app/blobfuse-proxy

# Copy init container script for installing the proxy as a host service.
RUN mkdir -p /deploy
COPY poc/csi-driver/deploy/onebox/install-proxy.sh /deploy/install-proxy.sh
RUN chmod +x /deploy/install-proxy.sh

ENTRYPOINT ["/app/csi-driver"]
