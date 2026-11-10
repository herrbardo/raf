# RAF — Requests and Friends

Cliente desktop colaborativo para enviar requests HTTP a distintas APIs y trabajar en equipo (similar a Postman).

- Cliente: Avalonia (.NET 10), para Windows y macOS
- Backend: ASP.NET Core 10 + PostgreSQL, desplegado en AWS (ALB existente → ECS Fargate). Ver [infra/](infra/README.md)

## Desarrollo

Requisitos: .NET SDK 10 y Docker.

```bash
.\scripts\dev-setup.ps1                 # genera .env y user-secrets; volver a correrlo si se agregan claves
docker compose up -d db                 # Postgres en localhost:5433
dotnet run --project src/Raf.Server     # API en http://localhost:5080, docs en /scalar
dotnet test                             # tests (levanta Postgres con Testcontainers)
```

Admin de desarrollo: `admin@raf.local`; su contraseña es `RAF_ADMIN_PASSWORD` en `.env`.

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

Firma: sin certificados el workflow publica igual, pero sin firmar. Los secretos que la activan están en [docs/plans/06-distribucion.md](docs/plans/06-distribucion.md#secretos-del-repositorio).

## Estructura

| Proyecto | Contenido |
|---|---|
| `src/Raf.Domain` | Entidades, tipos de request, herencia Request → Sample, resolución de variables |
| `src/Raf.Contracts` | DTOs compartidos entre cliente y servidor |
| `src/Raf.Server.Data` | EF Core, Identity, migraciones, sellado de `Version`/`ChangeSeq` |
| `src/Raf.Server` | API (Minimal APIs), auth JWT, permisos |
| `src/Raf.Client.Core` | Lógica del cliente sin UI: sesión, API, caché local y sincronización |
| `src/Raf.Client` | App de escritorio (Avalonia) y actualizaciones (Velopack) |
| `tests/*` | Tests de dominio e integración |

Documentación: [docs/Intro.md](docs/Intro.md) · Hoja de ruta: [docs/plans/README.md](docs/plans/README.md)
