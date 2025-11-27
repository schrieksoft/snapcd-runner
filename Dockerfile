FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0-preview-bookworm-slim

EXPOSE 80
EXPOSE 443

RUN apt-get update && \
    apt-get install -y --no-install-recommends git openssh-client wget curl

WORKDIR /app

ENV ASPNETCORE_URLS=http://*:80

# Copy pre-built binary directly from the GitHub Action workflow
ARG BINARY_NAME
COPY artifacts/SnapCd.Runner /app/SnapCd.Runner
COPY SnapCd.Runner/appsettings.json /app/appsettings.json

RUN chmod +x /app/SnapCd.Runner


ENTRYPOINT ["./SnapCd.Runner"]
