FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        clang \
        zlib1g-dev \
        binutils \
        git \
        procps \
    && rm -rf /var/lib/apt/lists/*
