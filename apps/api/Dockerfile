# Pinned .NET 10 LTS images; no host SDK required anywhere in this repo.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src/apps/api

COPY apps/api/Directory.Build.props ./
COPY apps/api/src/Ringfour.Api/Ringfour.Api.csproj src/Ringfour.Api/
RUN dotnet restore src/Ringfour.Api/Ringfour.Api.csproj

COPY apps/api/src/ src/
COPY packages/contracts/schemas/ /src/packages/contracts/schemas/
RUN dotnet publish src/Ringfour.Api/Ringfour.Api.csproj \
    --configuration Release \
    --no-restore \
    -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
RUN mkdir /storage && chown app:app /storage
USER app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Ringfour.Api.dll"]
