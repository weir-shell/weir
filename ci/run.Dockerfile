# The repo baked into the toolchain image — no bind mounts, so this works
# with remote docker daemons, FUSE checkouts, and SELinux alike.
FROM weir-ci
WORKDIR /work
COPY . /work
