# Build every native component against Debian 11's glibc 2.31 baseline.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk
FROM debian:bullseye-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates clang zlib1g-dev libicu67 libssl1.1 curl unzip python3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet PATH=/usr/share/dotnet:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# SQLite's NuGet binary requires glibc 2.34. Rebuild the same SQLite version
# from its checksum-pinned upstream source, rather than downgrading SQLite.
RUN curl -fsSL https://www.sqlite.org/2026/sqlite-amalgamation-3530400.zip -o /tmp/sqlite.zip \
    && echo '1e71ddf93849c6a6ecf58b827c0692073d2dd7ee40196158068f7b29f422e87d  /tmp/sqlite.zip' | sha256sum -c - \
    && unzip -q /tmp/sqlite.zip -d /tmp \
    && clang -O2 -fPIC -shared -pthread \
       -DSQLITE_ENABLE_COLUMN_METADATA -DSQLITE_ENABLE_FTS3 -DSQLITE_ENABLE_FTS4 \
       -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_RTREE -DSQLITE_ENABLE_MATH_FUNCTIONS \
       -DSQLITE_ENABLE_DBSTAT_VTAB -DSQLITE_ENABLE_SNAPSHOT \
       /tmp/sqlite-amalgamation-3530400/sqlite3.c -ldl -lm -o /tmp/libe_sqlite3.so
WORKDIR /source
COPY . .
ARG VERSION=0.2.0
RUN bash scripts/publish-linux.sh "$VERSION"
FROM scratch AS export
COPY --from=build /source/artifacts/publish/ /
