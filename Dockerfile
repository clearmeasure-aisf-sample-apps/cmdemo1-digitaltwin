FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG VERSION=1.0.0
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/DigitalTwin.Core/DigitalTwin.Core.csproj src/DigitalTwin.Core/
COPY src/DigitalTwin.Application/DigitalTwin.Application.csproj src/DigitalTwin.Application/
COPY src/DigitalTwin.GitHub/DigitalTwin.GitHub.csproj src/DigitalTwin.GitHub/
COPY src/DigitalTwin.Octopus/DigitalTwin.Octopus.csproj src/DigitalTwin.Octopus/
COPY src/DigitalTwin.Azure/DigitalTwin.Azure.csproj src/DigitalTwin.Azure/
COPY src/DigitalTwin.Probes/DigitalTwin.Probes.csproj src/DigitalTwin.Probes/
COPY src/DigitalTwin.Persistence/DigitalTwin.Persistence.csproj src/DigitalTwin.Persistence/
COPY src/DigitalTwin.Messaging/DigitalTwin.Messaging.csproj src/DigitalTwin.Messaging/
COPY src/DigitalTwin.Archive/DigitalTwin.Archive.csproj src/DigitalTwin.Archive/
COPY src/DigitalTwin.Api/DigitalTwin.Api.csproj src/DigitalTwin.Api/

RUN dotnet restore src/DigitalTwin.Api/DigitalTwin.Api.csproj

COPY src/ src/
RUN dotnet publish src/DigitalTwin.Api/DigitalTwin.Api.csproj \
    -c Release \
    -o /app/publish \
    -p:Version=$VERSION \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish .
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/_healthcheck || exit 1

ENTRYPOINT ["dotnet", "DigitalTwin.Api.dll"]
