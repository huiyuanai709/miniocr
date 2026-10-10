# Multi-stage Native AOT image (linux-x64) built from this repository.
# Default tier matches CI: IlcInstructionSet=avx2 (unsuffixed miniocr-linux-x64).
# AVX-512 hosts:
#   docker compose build --build-arg ILC_INSTRUCTION_SET=avx512v2
#   docker build -f Dockerfile.publish -t miniocr:local --build-arg ILC_INSTRUCTION_SET=avx512v2 .
#
# The build context must include external/ (git submodule update --init --recursive).
FROM ubuntu:24.04 AS build

ARG DOTNET_VERSION=11.0.100-rc.1.26425.128
ARG ILC_INSTRUCTION_SET=avx2

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_ROOT=/usr/share/dotnet

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        clang \
        curl \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --version "${DOTNET_VERSION}" --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm -f /tmp/dotnet-install.sh

WORKDIR /src
COPY . .
# Same publish line as .github/workflows/publish.yml for linux-x64 (avx2 / avx512v2).
RUN dotnet publish ./MiniOcr.csproj \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -o /out \
        -p:IlcInstructionSet="${ILC_INSTRUCTION_SET}" \
    && find /out -type f \( -name '*.dbg' -o -name '*.pdb' \) -delete

FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libfontconfig1 \
        libfreetype6 \
        libgomp1 \
        libicu74 \
        libssl3 \
        libvulkan1 \
        zlib1g \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/miniocr
COPY --from=build /out/ /opt/miniocr/
COPY config.docker.json /opt/miniocr/config.docker.json
COPY docker/entrypoint.sh /opt/miniocr/entrypoint.sh
RUN chmod 755 /opt/miniocr/MiniOcr /opt/miniocr/entrypoint.sh \
    && test -f /opt/miniocr/libSkiaSharp.so \
    && test -f /opt/miniocr/libpdfium.so

# NVIDIA_VISIBLE_DEVICES is set per service. Inside the container the only
# visible card is Vulkan device 0; do not pass the host index here.
ENV MINIOCR_CONFIG_PATH=/opt/miniocr/config.docker.json \
    MINIOCR_PORT=5080 \
    HOME=/opt/miniocr \
    LD_LIBRARY_PATH=/opt/miniocr \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility,graphics

EXPOSE 5080
ENTRYPOINT ["/opt/miniocr/entrypoint.sh"]
