# Plan 00 — Arquitectura y stack

Estado: **aprobado** (2026-11-06), con puntos menores pendientes al final.

Entorno detectado: .NET SDK 10.0.401, Docker 29.8, git 2.45, Node 24 (Windows 11).

## Decisiones tomadas

| Tema | Decisión |
|---|---|
| UI | Avalonia 11 |
| Backend | ASP.NET Core 10 + PostgreSQL, desplegado en **AWS: ALB existente (subpath) → ECS Fargate**, sobre una VPC existente |
| Autenticación | Cuentas propias, **creadas solo por un admin**. Sin registro abierto ni emails |
| Entornos y variables | Sí, **por workspace (equipo)** y sincronizados, secretos incluidos |
| Carpetas | Sí, anidables dentro de una App |
| Tiempo real | **SignalR por el ALB** (AppSync descartado al quitar API Gateway) |
| Repo | git + GitHub |

---

## 1. Framework de UI

| Opción | Win | Mac | Linux | Comentario |
|---|---|---|---|---|
| **Avalonia 11** ✅ | ✅ | ✅ | ✅ | XAML/MVVM, look idéntico en todas las plataformas, AvaloniaEdit para bodies JSON. |
| .NET MAUI | ✅ | ⚠️ | ❌ | Orientado a mobile. |
| Uno Platform | ✅ | ✅ | ✅ | Viable, más complejo. |
| Electron / Tauri | ✅ | ✅ | ✅ | Pierde el C# compartido con el backend. |

Librerías: CommunityToolkit.Mvvm, AvaloniaEdit, Microsoft.Extensions.Hosting (DI).

## 2. Stack

- **Cliente:** Avalonia 11, .NET 10, MVVM, SQLite local (EF Core) como caché offline.
- **Backend:** ASP.NET Core 10 (Minimal APIs), EF Core 10, PostgreSQL.
- **Ejecución de requests:** en el cliente (HttpClient). El servidor nunca actúa como proxy, así que funcionan endpoints de `localhost` y de redes internas.
- **Tests:** xUnit + Testcontainers (Postgres).
- **Dev local:** Postgres en Docker; el backend corre como app ASP.NET normal (`dotnet run`).

## 3. Infraestructura AWS

```
Cliente Avalonia ──HTTPS / WebSocket──> ALB existente (regla {PathPrefix}/*) ──> ECS Fargate: Raf.Server ──> RDS PostgreSQL
```

- **Red y entrada existentes:** el template recibe la VPC, las subredes y un ALB privado ya creado, que aloja varias apps bajo subpaths de un dominio gestionado fuera de este stack. RAF agrega su propio target group y una regla `path-pattern` en el listener (default `/raf/*`). El servidor quita el prefijo con `PathBase`.
- **Cómputo: ECS Fargate.** `Raf.Server` se empaqueta como imagen Docker (ECR) y corre igual en local y en AWS.
- **Base de datos:** RDS PostgreSQL 17 en subredes privadas, accesible solo desde las tareas ECS.
- **Secretos:** Secrets Manager (credenciales de la base, clave JWT, contraseña inicial del admin). KMS para cifrar las variables secretas.
- **Infraestructura como código:** **CloudFormation**, un único template ([`infra/raf.yaml`](../../infra/raf.yaml)). El despliegue lo hace el usuario a mano ([guía](../../infra/README.md)).

## 4. Solución única

```
src/
  Raf.Domain/        # Entidades y reglas (merge Request/Sample, resolución de variables)
  Raf.Contracts/     # DTOs compartidos cliente/servidor
  Raf.Server/        # API ASP.NET Core (Kestrel; Docker en ECS)
  Raf.Server.Data/   # EF Core + migraciones Postgres
  Raf.Client.Core/   # Ejecutor HTTP, sync, almacenamiento local, suscripción a eventos
  Raf.Client/        # Avalonia (Views/ViewModels)
tests/
  Raf.Domain.Tests/  Raf.Server.Tests/  Raf.Client.Core.Tests/
infra/
  raf.yaml           # CloudFormation: todo el backend en un template
  README.md          # Guía de despliegue
```

## 5. Autenticación (cuentas propias)

- **ASP.NET Core Identity** para usuarios y hashing de contraseñas (no `MapIdentityApi`: sus tokens dependen de Data Protection, y con varias tareas ECS habría que compartir el key ring).
- **Tokens propios:**
  - Access token **JWT** de 15 min, firmado con una clave guardada en Secrets Manager. Es stateless.
  - Refresh token opaco y rotativo, guardado hasheado en la base y revocable.
- **Alta de cuentas: solo el admin.** No hay registro abierto.
  - Rol de sistema **`Admin`** (distinto de los roles de workspace). El primer admin se crea al arrancar por primera vez, con usuario y contraseña tomados de la configuración (variable de entorno / Secrets Manager).
  - El admin crea el usuario con una **contraseña temporal** y se la pasa por fuera de la app. El usuario queda marcado `MustChangePassword` y debe cambiarla en el primer login.
  - El admin puede **resetear la contraseña** (genera una nueva temporal) y **deshabilitar** usuarios (revoca sus refresh tokens).
  - Cada usuario puede **cambiar su propia contraseña**.
  - El email se usa solo como identificador de login: **no se envían emails**.
- **Endpoints:**
  - Auth: `POST /auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/change-password`.
  - Admin: `GET/POST /admin/users`, `POST /admin/users/{id}/reset-password`, `POST /admin/users/{id}/disable` y `/enable`.
- **Cliente:** tokens en DPAPI (Windows) y Keychain (macOS).
- **Autorización:** por membresía de workspace con rol `Owner` / `Editor` / `Viewer`.
- **Miembros de workspace:** el Owner agrega usuarios ya existentes buscándolos por email o nombre. No hay invitaciones.

## 6. Sincronización y tiempo real

**Offline-first con servidor autoritativo:**

1. Cada entidad sincronizable tiene `Id` (Guid v7, generado en el cliente), `Version` y `IsDeleted` (borrado lógico).
2. Cada workspace tiene un contador monotónico `ChangeSeq`. Cada escritura lo incrementa y lo graba en la entidad.
3. **Pull:** `GET /workspaces/{id}/changes?since={seq}` devuelve las diferencias.
4. **Push:** el cliente envía la `Version` que conocía; si no coincide responde `409 Conflict`.
5. **Conflictos:** el cliente muestra la versión remota y la local, y el usuario elige.

**Notificaciones con SignalR:**

- Un hub en `Raf.Server`, con un grupo por workspace. Se publica por el mismo ALB, solo con transporte WebSocket.
- Después de cada escritura confirmada, el backend envía `{ "changeSeq": N }` al grupo. **No viaja contenido**, solo el aviso.
- El cliente se conecta con su JWT y el hub valida la membresía al workspace.
- Al recibir un aviso, el cliente hace pull. Si se pierde un aviso no se rompe nada: también hace pull al reconectar, al recuperar el foco y cada pocos minutos.
- En el código, todo esto queda detrás de `IChangeNotifier` (servidor) e `IChangeSubscriber` (cliente).

## 7. Modelo de datos

```
User ──< WorkspaceMember >── Workspace ──< Environment ──< Variable
              (Role)             │
                                 ├──< Variable (globales del workspace)
                                 └──< App ──< Folder (anidable) ──< Request ──< Sample
                                               └────────────────────< Request (en la raíz)
```

- **User:** Id, Email, DisplayName, IsAdmin, IsDisabled, MustChangePassword.
- **Workspace:** Id, Name, OwnerId, ChangeSeq.
- **WorkspaceMember:** WorkspaceId, UserId, Role.
- **Environment:** Id, WorkspaceId, Name (`dev`, `staging`, `prod`…).
- **Variable:** Id, Scope (Workspace global | Environment), Key, Value, IsSecret.
- **App:** Id, WorkspaceId, Name.
- **Folder:** Id, AppId, ParentFolderId?, Name, Order.
- **Request:** Id, AppId, FolderId?, Name, Order, Method, Url, Headers[], QueryParams[], Body (tipo + contenido), Auth.
- **Sample:** Id, RequestId, Name y **solo los overrides**.

**Herencia Request → Sample:**
- Escalares (Method, Url, Body, Auth): el Sample los reemplaza si están definidos.
- Colecciones por clave (Headers, Query): el Sample agrega o reemplaza por nombre, y puede **eliminar** una clave heredada.
- Si cambia el Request padre, el Sample recibe esos cambios en todo lo que no sobreescribió.

**Variables:**
- Sintaxis `{{nombre}}` en URL, headers, query, body y auth.
- Precedencia: **Environment activo > globales del Workspace**.
- El environment activo es una **elección local de cada usuario**; no se sincroniza.
- Variables `IsSecret`: se sincronizan con el equipo, se cifran en Postgres con KMS detrás de `IConfidentialManager`, se muestran enmascaradas en la UI y solo las ven los miembros con rol `Editor` o superior.

## 8. Administración desde el cliente

El cliente muestra las pantallas de gestión según el rol del usuario. El servidor valida siempre los permisos; la UI solo oculta lo que no corresponde.

**Admin (gestión del servidor):**
- Usuarios: listar, crear (con contraseña temporal), editar nombre y email, resetear contraseña, deshabilitar y habilitar, y dar o quitar el rol Admin.
- Workspaces: listar todos y **reasignar el Owner** (por si el Owner fue deshabilitado o dejó el equipo).
- Protección: no se puede quitar ni deshabilitar al último admin.

**Owner (gestión del workspace):**
- Renombrar y eliminar el workspace.
- Miembros: agregar usuarios existentes, quitar miembros y cambiar roles (`Editor` / `Viewer`).
- Transferir la propiedad a otro miembro.
- Environments y variables: crear, editar y eliminar (los `Editor` también pueden).

**Matriz de permisos por workspace:**

| Acción | Owner | Editor | Viewer |
|---|---|---|---|
| Ver apps, requests y samples, y ejecutarlos | ✅ | ✅ | ✅ |
| Crear, editar y borrar apps, carpetas, requests y samples | ✅ | ✅ | ❌ |
| Gestionar environments y variables | ✅ | ✅ | ❌ |
| Ver valores de variables secretas | ✅ | ✅ | ❌ |
| Gestionar miembros y roles, renombrar o eliminar el workspace | ✅ | ❌ | ❌ |
| Transferir la propiedad | ✅ | ❌ | ❌ |

Cualquier usuario puede crear un workspace y queda como su Owner.

## 9. Distribución del cliente

- **Velopack:** instalador + auto-update para Windows y macOS.
- **GitHub Actions** con runners Windows y macOS (no hace falta una Mac física).
- Firma de código (Windows) y notarización (Apple): se resuelve en el Plan 06.

---

## Pendientes (no bloquean el Plan 01)

1. **AWS:** perfil de AWS CLI local (aún sin instalar), región **`us-east-1`**. Se necesitan recién en el Plan 05.
