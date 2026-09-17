# Toolbox for running a MegaPDF pipeline unattended on a Linux server (#151):
# the core's toolchain, the .NET 8 SDK for the stress harness, git and gh.
# Built and used by tools/remote/run-151.sh; nothing is installed on the host.
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        cmake ninja-build clang g++ make python3 git curl ca-certificates jq procps \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /usr/share/keyrings/githubcli-archive-keyring.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
 && apt-get update && apt-get install -y --no-install-recommends gh \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
