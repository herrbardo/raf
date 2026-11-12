# Plan 05 — Despliegue AWS y tiempo real

Estado: **implementado**. El despliegue real y el tiempo real se validaron en Railway (Ticket-022); el despliegue en AWS sigue pendiente (Ticket-004). Depende de: Plan 03 (tiempo real) y Plan 04 (KMS).
Checkpoint humano al final: desplegar con AWS CLI (región `us-east-1`) y validar.

Se mantiene un **único template de CloudFormation**, que despliega el usuario.

## Alcance

1. **Tiempo real con SignalR:** implementación de `IChangeNotifier` e `IChangeSubscriber`; el polling queda como respaldo.
2. **KMS para los secretos:** una clave KMS en el template, permisos en `TaskRole` y un `IKeyWrapper` de KMS, activado con `Confidential:Provider`. El cifrado por sobre del Plan 04 (`EnvelopeConfidentialManager`) no cambia: KMS solo envuelve la clave de datos de cada workspace. Usa `workspaceId` como contexto de cifrado, así CloudTrail registra cada uso por workspace. Hasta que esto exista, el servidor no arranca en producción.
3. **Migraciones como paso de despliegue:** una tarea ECS de una sola ejecución, con `Database:MigrateOnStartup=false` en el servicio. Evita carreras cuando haya más de una tarea. Esa tarea también tiene que cifrar los secretos heredados (`ConfidentialSetup.EncryptLegacySecretsAsync`), que hoy corre después de migrar.
4. **Script de despliegue** `infra/deploy.ps1`: construye y sube la imagen, migra y actualiza el stack. No guarda credenciales.
5. **CI en GitHub Actions:** build, tests y escaneo de secretos (gitleaks) en cada push. El despliegue sigue siendo manual.

## Tiempo real: SignalR por el ALB

Decidido (D1). El ALB soporta WebSockets de forma nativa y no agrega piezas en AWS.

- Hub en `Raf.Server`, bajo el mismo `PathBase`. Valida el JWT y la membresía al unirse al grupo del workspace.
- Solo transporte WebSocket, sin negociación, para no requerir sesiones pegajosas en el ALB.
- El aviso lleva solo `{ changeSeq }`: el cliente hace pull. Si se pierde un aviso no se rompe nada.
- Con más de una tarea ECS, los avisos se reparten entre tareas con Postgres `LISTEN/NOTIFY`.
- Al deshabilitar un usuario, se cierran sus conexiones abiertas.

## Verificación

- `cfn-lint` sobre el template.
- Dos clientes contra el entorno desplegado: un cambio aparece en el otro sin refrescar.
- Corte de conexión: el cliente se recupera y no pierde cambios.
- Despliegue de una versión nueva sin corte, con la migración aplicada antes.
- Los secretos figuran cifrados en la base de RDS.

## Decisiones de implementación

- **Hub:** `ChangesHub` en `/hubs/changes`, solo WebSocket. El cliente manda la lista de sus workspaces con `Watch`, y el servidor acepta solo aquellos de los que es miembro. Además, cada conexión está en un grupo de su usuario: al agregarlo a un workspace o hacerlo Owner, recibe `Refresh` y sincroniza (todavía no estaba en el grupo de ese workspace).
- **Un solo camino para los avisos:** todo aviso pasa por `LISTEN/NOTIFY` de Postgres (`PostgresChangeBus`), haya una tarea o varias. Cada tarea escucha el canal y entrega a sus propias conexiones. Así, lo que se prueba en local es lo mismo que corre con varias tareas.
- **Usuario deshabilitado:** el aviso `UserDisabled` corta las conexiones abiertas en todas las tareas. Al reconectar, el JWT se rechaza por `UserStateCheck`. Quien deja de ser miembro sigue en el grupo de ese workspace hasta que su cliente vuelve a llamar a `Watch` en la próxima sincronización; mientras tanto solo recibe números de cambio, nunca datos.
- **Cliente:** `RealtimeChangeSubscriber` reconecta solo. Mientras no hay conexión, consulta cada 30 s; con conexión, queda solo el ciclo de 3 minutos del motor. Al reconectar dispara un pull para traer lo que se perdió.
- **KMS:** `KmsKeyWrapper` es otro `IKeyWrapper` (`Confidential:Provider=kms`). El cifrado por sobre del Plan 04 no cambia. El `TaskRole` solo puede usar la clave con un `workspaceId` en el contexto de cifrado. La clave se conserva al borrar el stack, porque sin ella los secretos de los snapshots no se recuperan.
- **Migraciones:** `dotnet Raf.Server.dll --migrate` migra, cifra los secretos heredados, crea el admin y termina. En AWS corre como `MigrationTaskDefinition`, con su propio parámetro `MigrationImageTag`: el script primero apunta esa tarea a la imagen nueva y la ejecuta, y recién después pasa el servicio a esa imagen. Así, una migración que falla no deja al servicio con código nuevo sobre una base vieja.
- **CI:** `.github/workflows/server.yml` corre build y todos los tests (con Docker del runner), `cfn-lint` sobre el template y gitleaks sobre todo el historial. Antes de subirlo, gitleaks se corrió en local sobre las ramas publicadas, sin hallazgos.
- **Verificado en local:** `cfn-lint` sin hallazgos. Hay tests de integración de avisos en tiempo real (a miembros y a usuarios recién agregados, rechazo de no miembros y de tokens inválidos, corte al deshabilitar) y del proveedor KMS con un KMS falso. El despliegue en AWS queda para el checkpoint.

## Requiere del usuario

- AWS CLI con un perfil local configurado.
- Parámetros de la VPC y del ALB existentes ([guía](../../infra/README.md)).
- Salida de las subredes de ECS hacia ECR, Secrets Manager y CloudWatch (y KMS).
