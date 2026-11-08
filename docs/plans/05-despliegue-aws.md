# Plan 05 — Despliegue AWS y tiempo real

Estado: **definido, sin ejecutar**. El template base ya existe ([`infra/raf.yaml`](../../infra/raf.yaml)). Depende de: Plan 03 (tiempo real) y Plan 04 (KMS).
Checkpoint humano al final: desplegar con AWS CLI (región `us-east-1`) y validar.

Se mantiene un **único template de CloudFormation**, que despliega el usuario.

## Alcance

1. **Tiempo real con SignalR:** implementación de `IChangeNotifier` e `IChangeSubscriber`; el polling queda como respaldo.
2. **`AwsConfidentialManager`:** una clave KMS en el template, permisos en `TaskRole` e implementación de `IConfidentialManager`. Usa `workspaceId` como contexto de cifrado, así CloudTrail registra cada uso por workspace.
3. **Migraciones como paso de despliegue:** una tarea ECS de una sola ejecución, con `Database:MigrateOnStartup=false` en el servicio. Evita carreras cuando haya más de una tarea.
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

## Requiere del usuario

- AWS CLI con un perfil local configurado.
- Parámetros de la VPC y del ALB existentes ([guía](../../infra/README.md)).
- Salida de las subredes de ECS hacia ECR, Secrets Manager y CloudWatch (y KMS).
