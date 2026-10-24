ARG BUILDPLATFORM
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY NuGet.Config ./
COPY ErgoStravaLogger/ErgoStravaLogger.csproj ErgoStravaLogger/
RUN case "$TARGETARCH" in amd64) rid=linux-x64;; arm64) rid=linux-arm64;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1;; esac \
    && dotnet restore ErgoStravaLogger/ErgoStravaLogger.csproj --runtime "$rid"
COPY ErgoStravaLogger/ ErgoStravaLogger/
RUN case "$TARGETARCH" in amd64) rid=linux-x64;; arm64) rid=linux-arm64;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1;; esac \
    && dotnet publish ErgoStravaLogger/ErgoStravaLogger.csproj -c Release --runtime "$rid" --self-contained false --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY LICENSE SECURITY.md THIRD-PARTY-NOTICES.md ./
ENV OUTPUT_DIRECTORY=/data
ENV LISTEN_URL=http://0.0.0.0:8080
EXPOSE 8080
EXPOSE 36866/tcp
EXPOSE 5353/udp
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "ErgoStravaLogger.dll"]
CMD ["web", "--config", "/config/config.json"]
