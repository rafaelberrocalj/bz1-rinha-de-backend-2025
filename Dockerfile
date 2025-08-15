FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
RUN mkdir /data
VOLUME /data
EXPOSE 9999

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet build "./bz1-rinha-de-backend-2025.csproj" -c Release -o /app/build
RUN dotnet publish "./bz1-rinha-de-backend-2025.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "bz1-rinha-de-backend-2025.dll"]