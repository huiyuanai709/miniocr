# Runtime image for the published linux-x64 Native AOT zip.
# Default asset is the AVX2 build. Override for AVX-512 hosts:
#   docker compose build --build-arg MINIOCR_ASSET=miniocr-linux-x64-avx512v2.zip
FROM ubuntu:24.04

ARG MINIOCR_VERSION=v0.0.16
ARG MINIOCR_ASSET=miniocr-linux-x64.zip

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        unzip \
        libfontconfig1 \
        libfreetype6 \
        libgomp1 \
        libicu74 \
        libssl3 \
        libvulkan1 \
        zlib1g \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/miniocr

RUN curl -fsSL -o /tmp/miniocr.zip \
        "https://github.com/huiyuanai709/miniocr/releases/download/${MINIOCR_VERSION}/${MINIOCR_ASSET}" \
    && unzip -q /tmp/miniocr.zip -d /tmp/miniocr-zip \
    && bin=$(find /tmp/miniocr-zip -type f -name MiniOcr | head -n 1) \
    && test -n "$bin" \
    && mv "$(dirname "$bin")"/* /opt/miniocr/ \
    && rm -rf /tmp/miniocr.zip /tmp/miniocr-zip \
    && chmod 755 /opt/miniocr/MiniOcr \
    && test -f /opt/miniocr/libSkiaSharp.so \
    && test -f /opt/miniocr/libpdfium.so

COPY config.docker.json /opt/miniocr/config.docker.json
COPY docker/entrypoint.sh /opt/miniocr/entrypoint.sh
RUN chmod 755 /opt/miniocr/entrypoint.sh

# NVIDIA_VISIBLE_DEVICES is set per service. Inside the container the only
# visible card is Vulkan device 0; do not pass the host index here.
ENV MINIOCR_CONFIG_PATH=/opt/miniocr/config.docker.json \
    MINIOCR_PORT=5080 \
    HOME=/opt/miniocr \
    LD_LIBRARY_PATH=/opt/miniocr \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility,graphics

EXPOSE 5080
ENTRYPOINT ["/opt/miniocr/entrypoint.sh"]
