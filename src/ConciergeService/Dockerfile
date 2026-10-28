# Multi-stage Dockerfile for ConciergeService
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ConciergeService/ConciergeService.csproj", "src/ConciergeService/"]
RUN dotnet restore "src/ConciergeService/ConciergeService.csproj"

COPY src/ConciergeService/ src/ConciergeService/
WORKDIR /src/src/ConciergeService
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ConciergeService.dll"]
