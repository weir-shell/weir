# The release image [D:container-image] — an ARTIFACT, not a product: it
# carries the SAME binary the release publishes (weir-<tag>-linux-x64 /
# -linux-arm64, the SHA256SUMS ones), placed by image.yml on the publish
# event. Never a build environment — no SDK, no source (the dev/battery
# container is ci/run.Dockerfile, a different artifact).
#
# cc-debian12 because the AOT binary is GLIBC-LINKED (ldd: libc, libm,
# ld-linux) — scratch needs a musl build weir does not do; a distro base
# would wrap a ~13 MB binary in ~120 MB of nothing. :nonroot (uid 65532,
# no shell, no package manager).
#
# ENTRYPOINT with no CMD: `docker run … weir:vX script.weir` runs a
# script, `docker run -it … weir:vX` is the REPL.
FROM gcr.io/distroless/cc-debian12:nonroot
ARG TARGETARCH
COPY dist/${TARGETARCH}/weir /weir
ENTRYPOINT ["/weir"]
