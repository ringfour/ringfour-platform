# Pinned .NET 10 LTS images; no host SDK required anywhere in this repo.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Ringfour.Api/Ringfour.Api.csproj src/Ringfour.Api/
RUN dotnet restore src/Ringfour.Api/Ringfour.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ringfour.Api/Ringfour.Api.csproj \
    --configuration Release \
    --no-restore \
    -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Ringfour.Api.dll"]
