# Plan 01 — Esqueleto + backend base

Estado: **implementado y aprobado**.
Checkpoint humano al final: revisar la API (OpenAPI/Scalar) y el modelo de datos.

## Alcance

1. **Solución** `Raf.slnx` con `Directory.Build.props` y gestión central de paquetes (`Directory.Packages.props`).
2. **Raf.Domain:** tipos de request (`RequestSpec`, `KeyValueItem`, `BodySpec`, `AuthSpec`), overrides de Sample, `SampleMerger` y `VariableResolver`, con tests unitarios.
3. **Raf.Contracts:** DTOs de la API.
4. **Raf.Server.Data:** `RafDbContext` (Identity + entidades), migraciones de Postgres y sellado automático de `Version`/`ChangeSeq` al guardar.
5. **Raf.Server:** Minimal APIs.
   - Auth: login, refresh (rotativo), logout, cambio de contraseña, `/me`.
   - Admin: usuarios (crear, editar, resetear contraseña, deshabilitar, habilitar), listado de workspaces y reasignación de Owner.
   - Workspaces: CRUD, miembros, roles y transferencia de propiedad.
   - Environments, variables, apps, carpetas, requests y samples: CRUD con permisos por rol.
   - Bootstrap del primer admin desde la configuración.
   - OpenAPI + UI de Scalar en desarrollo.
6. **Infra local:** `docker-compose.yml` con Postgres y `Dockerfile` del servidor (la misma imagen que usará ECS).
7. **Tests de integración:** `WebApplicationFactory` + Testcontainers con Postgres real.

Queda **fuera** de este plan: el feed de cambios y la sincronización (Plan 03), y el cifrado de secretos con KMS (Planes 04 y 05).

## Decisiones de implementación

- **Rutas planas por workspace:** `/workspaces/{ws}/apps|folders|requests|samples|environments|variables`. El permiso se valida siempre contra la membresía en `{ws}`, y el cliente sincroniza por workspace.
- **Ids:** los puede generar el cliente (Guid v7, campo `id` opcional en los create). Si no viene, lo genera el servidor.
- **Concurrencia optimista:** todo `PUT` lleva la `version` que conoce el cliente. Si no coincide, responde `409 Conflict`.
- **Borrado lógico:** `DELETE` marca `IsDeleted`. Borrar una carpeta o una app arrastra a sus hijos.
- **`ChangeSeq`:**
  - Un contador por workspace en la tabla `WorkspaceSequences`.
  - Se incrementa con un upsert (`INSERT … ON CONFLICT … RETURNING`) dentro de la misma transacción del guardado.
  - El lock de fila serializa las escrituras de un mismo workspace. Así un cambio con un número menor nunca se confirma después de uno con un número mayor, y el futuro `changes?since=` no pierde cambios.
- **Contraseña temporal:** el JWT lleva el claim `raf_pwd_change`. Mientras esté presente, solo se permiten `/me`, `/auth/change-password` y `/auth/logout`.
- **Usuario deshabilitado:** no puede hacer login ni refresh, y se revocan sus refresh tokens. El access token vigente vence en 15 minutos como máximo.
- **Variables secretas:** los `Viewer` reciben el valor enmascarado (`null` + `isSecret: true`).

## Resultado

- 37 tests en verde: 11 de dominio y 26 de integración contra Postgres real. Incluye escrituras concurrentes que verifican que `ChangeSeq` no se repita.
- La imagen Docker del servidor compila y corre contra Postgres (`docker compose --profile server up`).

## Cómo correrlo

```bash
.\scripts\dev-setup.ps1                 # una vez: genera .env y user-secrets
docker compose up -d db                 # Postgres en localhost:5433
dotnet run --project src/Raf.Server     # http://localhost:5080
# Docs interactivas: http://localhost:5080/scalar
# Admin de desarrollo: admin@raf.local; contraseña en RAF_ADMIN_PASSWORD (.env)
dotnet test                             # requiere Docker (Testcontainers)
```

El admin inicial se toma de `Bootstrap:AdminEmail` y `Bootstrap:AdminPassword` (en desarrollo: `user-secrets`, cargados por `scripts/dev-setup.ps1`).

## Notas para planes siguientes

- Las migraciones se aplican al arrancar (`Database:MigrateOnStartup`). En ECS con varias tareas conviene correrlas como paso de despliegue (Plan 05).
- Un usuario deshabilitado conserva su access token hasta 15 minutos.
- Las variables secretas se guardan en texto plano en la base; el cifrado va en los Planes 04 (`IConfidentialManager`) y 05 (KMS).
