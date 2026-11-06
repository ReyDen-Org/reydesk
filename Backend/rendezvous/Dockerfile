FROM golang:1.25 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/rendezvous ./cmd/rendezvous

FROM gcr.io/distroless/static:nonroot
COPY --from=build /out/rendezvous /rendezvous
USER nonroot:nonroot
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=4s --start-period=20s --retries=3 CMD ["/rendezvous", "healthcheck"]
ENTRYPOINT ["/rendezvous"]
