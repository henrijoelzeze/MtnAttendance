# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MtnAttendance.csproj ./
RUN dotnet restore MtnAttendance.csproj
COPY . .
RUN dotnet publish MtnAttendance.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
# LDAPS uses system CA certificates; add the enterprise CA to the host or image
# via a separately managed trusted configuration, not an insecure TLS bypass.
USER root
RUN apk add --no-cache ca-certificates libldap krb5-libs icu-libs
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/keys && chown -R app:app /app/keys
USER app
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","MtnAttendance.dll"]
