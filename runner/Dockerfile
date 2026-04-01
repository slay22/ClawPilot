# claw-pilot-runner — short-lived PR review container image
#
# Layer 1: base runtimes + git
# Build once, push to your registry, reference via PR_RUNNER_IMAGE env var.
#
# docker build -t claw-pilot-runner:latest runner/

FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# ── System packages ──────────────────────────────────────────────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    git \
    jq \
    # .NET SDK (for C# projects)
    && install -d /usr/share/dotnet \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/local/bin/dotnet-dir \
    && ln -s /usr/local/bin/dotnet-dir/dotnet /usr/local/bin/dotnet \
    # Node.js 22 LTS (for JS/TS projects)
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    # Python + pip (for Python projects)
    && apt-get install -y --no-install-recommends python3 python3-pip python3-venv \
    # Pre-install ruff at a pinned version to avoid runtime PyPI installs
    && pip3 install --break-system-packages ruff==0.11.2 \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# ── Output directory (volume mount point) ────────────────────────────────────
RUN mkdir -p /output /workspace

WORKDIR /workspace

# ── Entrypoint ────────────────────────────────────────────────────────────────
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
