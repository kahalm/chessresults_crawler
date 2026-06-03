FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ChessResultsCrawler.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# curl für den Docker-Healthcheck (GET /api/health); --no-install-recommends hält das Image schlank.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN adduser --disabled-password --no-create-home appuser

WORKDIR /app
COPY --from=build /app/publish .

# Bindung auf :8080 kommt aus dem Base-Image (ASPNETCORE_HTTP_PORTS=8080) —
# kein ASPNETCORE_URLS noetig (vermeidet "Overriding HTTP_PORTS"-Warning beim Start).
EXPOSE 8080

USER appuser
ENTRYPOINT ["dotnet", "ChessResultsCrawler.dll"]
