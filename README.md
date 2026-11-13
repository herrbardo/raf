# RAF — Requests and Friends

Cliente colaborativo para enviar requests HTTP a distintas APIs y trabajar en equipo (similar a Postman), de escritorio y web.

- Cliente: Avalonia (.NET 10), para Windows y macOS, y la misma interfaz en el navegador con WebAssembly (ver [plan 09](docs/plans/09-cliente-web.md))
- Backend: ASP.NET Core 10 + PostgreSQL, desplegado en AWS (ALB existente → ECS Fargate, ver [infra/](infra/README.md)) o en Railway (ver [infra/railway/](infra/railway/README.md))

## Desarrollo

Requisitos: .NET SDK 10 y Docker.

```bash
.\scripts\dev-setup.ps1                 # genera .env y user-secrets; volver a correrlo si se agregan claves
docker compose up -d db                 # Postgres en localhost:5433
dotnet run --project src/Raf.Server     # API en http://localhost:5080, docs en /scalar
dotnet test                             # tests (levanta Postgres con Testcontainers)
```

Admin de desarrollo: `admin@raf.local`; su contraseña es `RAF_ADMIN_PASSWORD` en `.env`.

Cliente web (una vez: `dotnet workload install wasm-tools`):

```bash
dotnet run --project src/Raf.Client.Browser   # http://localhost:5090, se conecta al servidor que indiques
```

En desarrollo, el servidor ya acepta ese origen (`Cors:AllowedOrigins` en `appsettings.Development.json`). Los requests que envía la web pasan por el relay del servidor (`POST /relay`), que no alcanza direcciones internas como `localhost` salvo las de `Relay:AllowedPrivateHosts`.

**Reglas:** no se versionan claves ni nombres de infraestructura existente. Ver [CLAUDE.md](CLAUDE.md).

Nueva migración:

```bash
dotnet tool restore
dotnet ef migrations add <Nombre> --project src/Raf.Server.Data --startup-project src/Raf.Server
```

## Publicar una versión del cliente

```bash
git tag v0.2.0
git push origin v0.2.0
```

El workflow [release.yml](.github/workflows/release.yml) compila, empaqueta con Velopack y publica en GitHub Releases. Las apps instaladas encuentran la versión nueva al arrancar, la descargan en segundo plano y ofrecen reiniciar.

Para obligar a los clientes viejos a actualizarse, se sube `MinClientVersion` en el stack (`Clients:MinVersion` en el servidor) **después** de publicar la versión nueva: los clientes anteriores reciben 426 y muestran la pantalla de actualización.

Firma: macOS se publica firmado y notarizado. Windows se publica sin firmar por decisión (D8), así que SmartScreen advierte al instalar. Los secretos que activan cada firma están en [docs/plans/06-distribucion.md](docs/plans/06-distribucion.md#secretos-del-repositorio).

## Estructura

| Proyecto | Contenido |
|---|---|
| `src/Raf.Domain` | Entidades, tipos de request, herencia Request → Sample, resolución de variables |
| `src/Raf.Contracts` | DTOs compartidos entre cliente y servidor |
| `src/Raf.Server.Data` | EF Core, Identity, migraciones, sellado de `Version`/`ChangeSeq` |
| `src/Raf.Server` | API (Minimal APIs), auth JWT, permisos |
| `src/Raf.Client.Core` | Lógica del cliente sin UI: sesión, API, caché local, sincronización y ejecución de requests (directa o por el relay) |
| `src/Raf.Client.UI` | Interfaz compartida (Avalonia): vistas, ViewModels, estilos y diálogos |
| `src/Raf.Client` | App de escritorio y actualizaciones (Velopack) |
| `src/Raf.Client.Browser` | Cliente web (WebAssembly), servido con Caddy. Fuera de `Raf.slnx`: necesita `wasm-tools` |
| `tests/*` | Tests de dominio e integración |

Documentación: [docs/Intro.md](docs/Intro.md) · Hoja de ruta: [docs/plans/README.md](docs/plans/README.md)
