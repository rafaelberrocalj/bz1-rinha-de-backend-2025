FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
EXPOSE 9999

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY ["bz1-rinha-de-backend-2025.csproj", "."]
RUN dotnet restore "./bz1-rinha-de-backend-2025.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./bz1-rinha-de-backend-2025.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "./bz1-rinha-de-backend-2025.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "bz1-rinha-de-backend-2025.dll"]