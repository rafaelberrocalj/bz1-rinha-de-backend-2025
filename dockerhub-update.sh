#!/bin/zsh

## create podman machine
# podman machine init --rootful --now --cpus 2 --disk-size 20 -m 4096

## test podman amd64
# podman run --rm --arch=amd64 node node --help

echo "~ build and tag image"
podman build --platform linux/amd64,linux/arm64 -t rafaelberrocalj/bz1:rinha-de-backend-2025-latest .
podman buildx build --platform linux/amd64,linux/arm64 -t rafaelberrocalj/bz1:rinha-de-backend-2025-latest .

echo "~ push image to dockerhub"
podman push rafaelberrocalj/bz1:rinha-de-backend-2025-latest

echo "~ check image arch"
podman pull --platform=linux/amd64 rafaelberrocalj/bz1:rinha-de-backend-2025-latest
podman image inspect rafaelberrocalj/bz1:rinha-de-backend-2025-latest | grep -i arch
podman inspect rafaelberrocalj/bz1:rinha-de-backend-2025-latest | grep -i arch
