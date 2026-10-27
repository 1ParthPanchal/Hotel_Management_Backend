# Multi-stage Dockerfile for NotificationService
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/NotificationService/NotificationService.csproj", "src/NotificationService/"]
RUN dotnet restore "src/NotificationService/NotificationService.csproj"

COPY src/NotificationService/ src/NotificationService/
WORKDIR /src/src/NotificationService
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "NotificationService.dll"]
