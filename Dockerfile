FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG VERSION=1.0.0
WORKDIR /src

COPY global.json Directory.Build.props cmdemo1-digitaltwin.sln ./
COPY src/DigitalTwin.Domain/DigitalTwin.Domain.csproj src/DigitalTwin.Domain/
COPY src/DigitalTwin.Application/DigitalTwin.Application.csproj src/DigitalTwin.Application/
COPY src/DigitalTwin.Infrastructure/DigitalTwin.Infrastructure.csproj src/DigitalTwin.Infrastructure/
COPY src/DigitalTwin.Api/DigitalTwin.Api.csproj src/DigitalTwin.Api/
COPY tests/DigitalTwin.Tests/DigitalTwin.Tests.csproj tests/DigitalTwin.Tests/

RUN dotnet restore cmdemo1-digitaltwin.sln

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
