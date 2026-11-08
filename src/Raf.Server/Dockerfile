# Imagen del servidor (la misma que se despliega en ECS Fargate). Construir desde la raíz del repo.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Raf.Domain/Raf.Domain.csproj src/Raf.Domain/
COPY src/Raf.Contracts/Raf.Contracts.csproj src/Raf.Contracts/
COPY src/Raf.Server.Data/Raf.Server.Data.csproj src/Raf.Server.Data/
COPY src/Raf.Server/Raf.Server.csproj src/Raf.Server/
RUN dotnet restore src/Raf.Server/Raf.Server.csproj
COPY src/ src/
RUN dotnet publish src/Raf.Server/Raf.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Raf.Server.dll"]
