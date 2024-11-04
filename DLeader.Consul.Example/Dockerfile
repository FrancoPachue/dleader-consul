FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copiar solución y proyectos
COPY ["../DLeader.Consul/", "DLeader.Consul/"]
COPY ["./" , "DLeader.Consul.Example/"]

# Restaurar y build
WORKDIR "/src/DLeader.Consul.Example"
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DLeader.Consul.Example.dll"]