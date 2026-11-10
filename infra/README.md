# Infraestructura AWS (CloudFormation)

> Para desplegar en Railway en lugar de AWS, ver [railway/README.md](railway/README.md).

Un solo template, [`raf.yaml`](raf.yaml), despliega el backend sobre una **VPC y un ALB que ya existen**:

```
Cliente ──HTTPS / WebSocket──> ALB existente (regla en {PathPrefix}/*) ──> Target group ──> ECS Fargate (servidor) ──> RDS PostgreSQL 17
                                                                                               └──> KMS (secretos)
```

**Lo que se recibe por parámetro** (ya existe):

| Parámetro | Para qué |
|---|---|
| `VpcId` | VPC del ALB. |
| `ServiceSubnetIds` | Subredes de las tareas ECS. Necesitan salida a internet (NAT) o VPC endpoints para ECR, Secrets Manager, CloudWatch Logs y KMS. |
| `AssignPublicIp` | `ENABLED` solo si `ServiceSubnetIds` son públicas. Default `DISABLED`. |
| `DbSubnetIds` | Subredes privadas para RDS (mínimo 2, en zonas distintas). |
| `AlbListenerArn` | Listener del ALB (HTTP o HTTPS) donde se agrega la regla. |
| `AlbSecurityGroupId` | Security group del ALB: las tareas solo aceptan tráfico desde él. |
| `PathPrefix` | Subpath de la API. Default `/raf`. |
| `HostHeader` | Opcional: dominio de la regla, si el listener atiende varios. |
| `ListenerRulePriority` | Prioridad de la regla; no puede repetirse en ese listener. |
| `AdminEmail` | Email del admin inicial. |
| `MinClientVersion` | Opcional: versión mínima del cliente. Default `0.0.0` (acepta todas). Subirla después de publicar la versión nueva; ver el [README](../README.md#publicar-una-versión-del-cliente). |

**Lo que crea el template:** security groups de las tareas y de la base, RDS PostgreSQL 17 (`db.t4g.micro`, 20 GB gp3 cifrado, backups de 7 días, snapshot al borrar), los secretos en Secrets Manager (credenciales de la base, clave JWT y contraseña inicial del admin), una clave KMS para las variables secretas (con rotación anual), el repositorio ECR, el cluster, las task definitions del servidor y de las migraciones, el servicio de ECS Fargate, logs en CloudWatch, el target group y la regla del listener.

**Subpath:** el ALB no recorta el prefijo, así que el servidor recibe `PathBase=/raf` y lo quita él mismo. La API queda en `https://<dominio>/raf/...` y el health check del target group usa `/raf/health`.

**Tiempo real:** los avisos de cambios van por WebSocket en `https://<dominio>/raf/hubs/changes`, por la misma regla del ALB. El cliente no negocia el transporte, así que no hacen falta sesiones pegajosas. Con varias tareas, los avisos se reparten entre ellas con `LISTEN/NOTIFY` de Postgres. Si el WebSocket se corta, el cliente sigue sincronizando por polling.

**Migraciones:** el servicio arranca con `Database:MigrateOnStartup=false`. Las migraciones corren antes, en una tarea de una sola ejecución (`raf-<env>-migrate`) que lanza `deploy.ps1`. Esa tarea también cifra los secretos heredados y crea el admin inicial.

**Costo aproximado** en `us-east-1`, sin contar el ALB que ya existe: **~USD 27/mes** (RDS ~15, Fargate ~9, secretos ~1.2, KMS ~1).

## 0. Instalar y configurar AWS CLI (una vez)

```powershell
winget install -e --id Amazon.AWSCLI
# Cerrar y abrir la terminal, luego:
aws configure --profile <tu-perfil>          # o: aws configure sso --profile <tu-perfil>
#   Default region name: us-east-1
aws sts get-caller-identity --profile <tu-perfil>
```

Los comandos siguientes son para PowerShell y se ejecutan desde la raíz del repo:

```powershell
$env:AWS_PROFILE = "<tu-perfil>"
$env:AWS_REGION  = "us-east-1"
$stack = "raf-dev"
```

### Datos útiles del ALB existente

```powershell
# Listeners del ALB (para AlbListenerArn)
aws elbv2 describe-listeners --load-balancer-arn <arn-del-alb> --query "Listeners[].[ListenerArn,Protocol,Port]" --output table
# Security groups del ALB (para AlbSecurityGroupId)
aws elbv2 describe-load-balancers --load-balancer-arns <arn-del-alb> --query "LoadBalancers[0].SecurityGroups"
# Prioridades ya usadas en el listener (elegir otra para ListenerRulePriority)
aws elbv2 describe-rules --listener-arn <arn-del-listener> --query "Rules[].Priority"
# Subredes de la VPC
aws ec2 describe-subnets --filters Name=vpc-id,Values=<vpc-id> --query "Subnets[].[SubnetId,AvailabilityZone,MapPublicIpOnLaunch,Tags[?Key=='Name']|[0].Value]" --output table
```

## 1. Primer despliegue

**a) Crear el stack.** El servicio arranca con 0 tareas porque todavía no hay imagen. Tarda unos 10–15 minutos, la mayor parte por RDS.

```powershell
aws cloudformation deploy `
  --template-file infra/raf.yaml `
  --stack-name $stack `
  --capabilities CAPABILITY_IAM `
  --parameter-overrides `
    VpcId=vpc-xxxxxxxx `
    ServiceSubnetIds=subnet-aaaa,subnet-bbbb `
    DbSubnetIds=subnet-cccc,subnet-dddd `
    AlbListenerArn=arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/mi-alb/xxxx/yyyy `
    AlbSecurityGroupId=sg-xxxxxxxx `
    ListenerRulePriority=50 `
    AdminEmail=tu-email@dominio.com
```

**b) Subir la imagen, migrar y encender el servicio.** [`deploy.ps1`](deploy.ps1) construye la imagen con el commit actual como tag, la sube a ECR, corre la tarea de migraciones y espera a que termine bien. Recién entonces pasa el servicio a la imagen nueva. Los parámetros del stack que no toca conservan su valor anterior.

```powershell
.\infra\deploy.ps1 -Stack $stack -DesiredCount 1
```

**c) Verificar y obtener la contraseña del admin.**

```powershell
curl.exe https://<tu-dominio>/raf/health          # -> Healthy

$secret = aws cloudformation describe-stacks --stack-name $stack `
  --query "Stacks[0].Outputs[?OutputKey=='AdminPasswordSecretArn'].OutputValue" --output text
aws secretsmanager get-secret-value --secret-id $secret --query SecretString --output text
```

Con `AdminEmail` y esa contraseña se hace el primer login; el servidor pide cambiarla.

## 2. Desplegar una nueva versión del servidor

```powershell
.\infra\deploy.ps1 -Stack $stack
```

Si la migración falla, el script se detiene y el servicio sigue con la versión anterior. Si no, ECS hace un despliegue sin corte; si las tareas nuevas no pasan el health check, vuelve solo a la versión anterior.

Las migraciones tienen que ser compatibles con la versión anterior del servidor, que sigue atendiendo mientras arrancan las tareas nuevas: agregar columnas o tablas sí, borrar o renombrar lo que la versión anterior usa, no (eso va en un despliegue posterior).

## 3. Diagnóstico

```powershell
aws logs tail "/ecs/raf-dev-server" --follow
aws logs tail "/ecs/raf-dev-server" --log-stream-name-prefix migrate   # tareas de migraciones
aws ecs describe-services --cluster raf-dev --services server --query "services[0].events[:5]"
$tg = aws cloudformation describe-stacks --stack-name $stack `
  --query "Stacks[0].Outputs[?OutputKey=='TargetGroupArn'].OutputValue" --output text
aws elbv2 describe-target-health --target-group-arn $tg
```

Si las tareas no arrancan con `CannotPullContainerError` o `ResourceInitializationError`, las subredes de `ServiceSubnetIds` no tienen salida a ECR o Secrets Manager (falta NAT o VPC endpoints). Si arrancan pero fallan al leer o guardar variables secretas, falta salida a KMS.

Los secretos guardados en la base se pueden comprobar desde una tarea con acceso a RDS: en `"Variables"`, toda fila con `"IsSecret" = true` tiene un `"Value"` que empieza con `enc:v1:`.

## 4. Borrar todo

```powershell
aws ecr batch-delete-image --repository-name raf-dev-server `
  --image-ids (aws ecr list-images --repository-name raf-dev-server --query "imageIds[].{imageTag:imageTag}" --output json | ConvertFrom-Json | ForEach-Object { "imageTag=$($_.imageTag)" })
aws cloudformation delete-stack --stack-name $stack
```

Borrar el stack quita la regla y el target group del ALB, pero no toca el ALB. RDS deja un snapshot final.

**La clave KMS se conserva** (`DeletionPolicy: Retain`): sin ella, las variables secretas del snapshot no se pueden descifrar. Si de verdad no se va a restaurar nada, se programa su borrado a mano (KMS espera entre 7 y 30 días):

```powershell
aws kms schedule-key-deletion --key-id <SecretsKeyArn> --pending-window-in-days 30
```
