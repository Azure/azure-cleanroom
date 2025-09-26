# Image for building the executable.
FROM mcr.microsoft.com/oss/go/microsoft/golang:1.24.4 AS build-image

# Install prerequisities.
RUN apt-get update -y && \
    DEBIAN_FRONTEND=noninteractive apt-get -y --no-install-recommends install \
    software-properties-common build-essential

# Set the working directory
WORKDIR /app

# Download dependencies. If go.mod/sum files are unchanged then layer caching optimization kicks in.
COPY src/go.mod .
COPY src/go.sum .
RUN go mod download

# Copy the source.
COPY src/ src/

# Build.
WORKDIR /app/src
RUN go build -o /app/main

# Optimize the final image size by creating an image with only the executable.
FROM mcr.microsoft.com/oss/go/microsoft/golang:1.24.4

COPY --from=build-image /app/main ./main
RUN chmod +x ./main
