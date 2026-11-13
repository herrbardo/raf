# Imagen del cliente web: compila a WebAssembly y sirve los archivos estáticos con Caddy. Construir desde la raíz
# del repo.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# El workload enlaza SQLite y Skia en el runtime de WebAssembly; en Linux usa el Python del sistema.
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
RUN dotnet workload install wasm-tools
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Raf.Domain/Raf.Domain.csproj src/Raf.Domain/
COPY src/Raf.Contracts/Raf.Contracts.csproj src/Raf.Contracts/
COPY src/Raf.Client.Core/Raf.Client.Core.csproj src/Raf.Client.Core/
COPY src/Raf.Client.UI/Raf.Client.UI.csproj src/Raf.Client.UI/
COPY src/Raf.Client.Browser/Raf.Client.Browser.csproj src/Raf.Client.Browser/
RUN dotnet restore src/Raf.Client.Browser/Raf.Client.Browser.csproj
COPY src/ src/
RUN dotnet publish src/Raf.Client.Browser/Raf.Client.Browser.csproj -c Release -o /app --no-restore -p:NoWarn=WASM0001

# 2.9: la 2.10 responde 206 (contenido parcial) a pedidos sin Range cuando sirve los archivos precomprimidos.
FROM caddy:2.9-alpine
COPY src/Raf.Client.Browser/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /app/wwwroot /srv
EXPOSE 8080
