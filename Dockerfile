# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG BUILDTIME
ARG VERSION
ARG REVISION

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["GeoLocationFilter.csproj", "."]
RUN dotnet restore "./GeoLocationFilter.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./GeoLocationFilter.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./GeoLocationFilter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

LABEL org.opencontainers.image.created=${BUILDTIME}
LABEL org.opencontainers.image.version=${VERSION}
LABEL org.opencontainers.image.revision=${REVISION}
LABEL org.opencontainers.image.title="GeoLocationFilter API"
LABEL org.opencontainers.image.description="Api for geo-filtering requests"
LABEL org.opencontainers.image.source="https://github.com/mklouda4/geolocationfilter"
LABEL org.opencontainers.image.url="https://github.com/mklouda4/geolocationfilter"
LABEL org.opencontainers.image.documentation="https://github.com/mklouda4/geolocationfilter#readme"
LABEL org.opencontainers.image.vendor="mklouda4"
LABEL org.opencontainers.image.licenses="MIT"

# Environment variables
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "GeoLocationFilter.dll"]