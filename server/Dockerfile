FROM golang:1.27.1-alpine3.24 AS builder

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download

COPY . .
ARG TARGETOS=linux
ARG TARGETARCH
RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build -trimpath -ldflags="-s -w" -o /out/clipbridge-server ./cmd/server
RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build -trimpath -ldflags="-s -w" -o /out/clipbridge-admin ./cmd/admin

FROM alpine:3.24
RUN apk add --no-cache ca-certificates
RUN mkdir -p /var/lib/clipbridge/images && chown -R 65532:65532 /var/lib/clipbridge
COPY --from=builder /out/clipbridge-server /usr/local/bin/clipbridge-server
COPY --from=builder /out/clipbridge-admin /usr/local/bin/clipbridge-admin

USER 65532:65532
ENV CLIPBRIDGE_IMAGE_DIR=/var/lib/clipbridge/images
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/local/bin/clipbridge-server"]
