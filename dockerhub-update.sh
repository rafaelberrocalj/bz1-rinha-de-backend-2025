#!/bin/zsh

podman build --platform linux/amd64 -t rafaelberrocalj/bz1:rinha-de-backend-2025-latest .
podman push rafaelberrocalj/bz1:rinha-de-backend-2025-latest
