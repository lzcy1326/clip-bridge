FROM golang:1.27.1-alpine3.24 AS builder

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download

COPY . .
ARG TARGETOS=linux
ARG TARGETARCH
RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build -trimpath -ldflags="-s -w" -o /out/clipbridge-server ./cmd/server

FROM alpine:3.24
RUN apk add --no-cache ca-certificates
COPY --from=builder /out/clipbridge-server /usr/local/bin/clipbridge-server

USER 65532:65532
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/local/bin/clipbridge-server"]
