FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ChessResultsCrawler.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl für den Docker-Healthcheck (GET /api/health); --no-install-recommends hält das Image schlank.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN adduser --disabled-password --no-create-home appuser

WORKDIR /app
COPY --from=build /app/publish .

# Build-Herkunft (vom CI als --build-arg gesetzt) → als ENV, von GET /api/health/build-info
# ausgeliefert, damit RookHubs Admin-CI-Seite den laufenden Crawler-Build-Run markieren kann.
ARG GIT_SHA=
ARG GIT_REF=
ENV BUILD_GIT_SHA=$GIT_SHA
ENV BUILD_GIT_REF=$GIT_REF

# Bindung auf :8080 kommt aus dem Base-Image (ASPNETCORE_HTTP_PORTS=8080) —
# kein ASPNETCORE_URLS noetig (vermeidet "Overriding HTTP_PORTS"-Warning beim Start).
EXPOSE 8080

USER appuser
ENTRYPOINT ["dotnet", "ChessResultsCrawler.dll"]
