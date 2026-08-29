# Build the embedded UI once, then cross-publish the self-contained binary for buildx's
# TARGETARCH. The final image contains only the native Vessel executable and its /data state.
FROM node:22-bookworm-slim AS frontend
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY . ./
COPY --from=frontend /source/frontend/dist ./frontend/dist
RUN dotnet publish src/Vessel/Vessel.csproj -c Release -r linux-${TARGETARCH} --self-contained \
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:SkipFrontendBuild=true -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /data
COPY --from=publish /out/Vessel /vessel
EXPOSE 4550
VOLUME ["/data"]
ENTRYPOINT ["/vessel", "--config", "/data/vessel.json"]
