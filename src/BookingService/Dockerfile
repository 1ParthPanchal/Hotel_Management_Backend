# Multi-stage Dockerfile for BookingService
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/BookingService/BookingService.csproj", "src/BookingService/"]
RUN dotnet restore "src/BookingService/BookingService.csproj"

COPY src/BookingService/ src/BookingService/
WORKDIR /src/src/BookingService
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "BookingService.dll"]
