# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MtnAttendance.csproj ./
RUN dotnet restore MtnAttendance.csproj
COPY . .
RUN dotnet publish MtnAttendance.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# LDAPS uses system CA certificates; add the enterprise CA to the host or image
# via a separately managed trusted configuration, not an insecure TLS bypass.
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libldap-2.5-0 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/keys && chown -R app:app /app/keys
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","MtnAttendance.dll"]
