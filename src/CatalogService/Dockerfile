# Multi-stage Dockerfile for CatalogService
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/CatalogService/CatalogService.csproj", "src/CatalogService/"]
RUN dotnet restore "src/CatalogService/CatalogService.csproj"

COPY src/CatalogService/ src/CatalogService/
WORKDIR /src/src/CatalogService
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CatalogService.dll"]
