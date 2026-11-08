# Infraestructura AWS (CloudFormation)

Un solo template, [`raf.yaml`](raf.yaml), despliega el backend sobre una **VPC y un ALB que ya existen**:

```
Cliente ──> ALB existente (regla en {PathPrefix}/*) ──> Target group ──> ECS Fargate (servidor) ──> RDS PostgreSQL 17
```

**Lo que se recibe por parámetro** (ya existe):

| Parámetro | Para qué |
|---|---|
| `VpcId` | VPC del ALB. |
| `ServiceSubnetIds` | Subredes de las tareas ECS. Necesitan salida a internet (NAT) o VPC endpoints para ECR, Secrets Manager y CloudWatch Logs. |
| `AssignPublicIp` | `ENABLED` solo si `ServiceSubnetIds` son públicas. Default `DISABLED`. |
| `DbSubnetIds` | Subredes privadas para RDS (mínimo 2, en zonas distintas). |
| `AlbListenerArn` | Listener del ALB (HTTP o HTTPS) donde se agrega la regla. |
| `AlbSecurityGroupId` | Security group del ALB: las tareas solo aceptan tráfico desde él. |
| `PathPrefix` | Subpath de la API. Default `/raf`. |
| `HostHeader` | Opcional: dominio de la regla, si el listener atiende varios. |
| `ListenerRulePriority` | Prioridad de la regla; no puede repetirse en ese listener. |
| `AdminEmail` | Email del admin inicial. |

**Lo que crea el template:** security groups de las tareas y de la base, RDS PostgreSQL 17 (`db.t4g.micro`, 20 GB gp3 cifrado, backups de 7 días, snapshot al borrar), los secretos en Secrets Manager (credenciales de la base, clave JWT y contraseña inicial del admin), el repositorio ECR, el cluster, la task definition y el servicio de ECS Fargate, logs en CloudWatch, el target group y la regla del listener.

**Subpath:** el ALB no recorta el prefijo, así que el servidor recibe `PathBase=/raf` y lo quita él mismo. La API queda en `https://<dominio>/raf/...` y el health check del target group usa `/raf/health`.

**Costo aproximado** en `us-east-1`, sin contar el ALB que ya existe: **~USD 26/mes** (RDS ~15, Fargate ~9, secretos ~1.2).

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

**b) Construir y subir la imagen del servidor.**

```powershell
$repo = aws cloudformation describe-stacks --stack-name $stack `
  --query "Stacks[0].Outputs[?OutputKey=='RepositoryUri'].OutputValue" --output text
aws ecr get-login-password | docker login --username AWS --password-stdin $repo.Split('/')[0]

$tag = git rev-parse --short HEAD
docker build -f src/Raf.Server/Dockerfile -t "${repo}:${tag}" .
docker push "${repo}:${tag}"
```

**c) Encender el servicio.** Los parámetros que no se pasan conservan su valor anterior.

```powershell
aws cloudformation deploy `
  --template-file infra/raf.yaml `
  --stack-name $stack `
  --capabilities CAPABILITY_IAM `
  --parameter-overrides ImageTag=$tag DesiredCount=1
```

**d) Verificar y obtener la contraseña del admin.**

```powershell
curl.exe https://<tu-dominio>/raf/health          # -> Healthy

$secret = aws cloudformation describe-stacks --stack-name $stack `
  --query "Stacks[0].Outputs[?OutputKey=='AdminPasswordSecretArn'].OutputValue" --output text
aws secretsmanager get-secret-value --secret-id $secret --query SecretString --output text
```

Con `AdminEmail` y esa contraseña se hace el primer login; el servidor pide cambiarla.

## 2. Desplegar una nueva versión del servidor

Repetir **1b** y **1c** con el nuevo `$tag`. ECS hace un despliegue sin corte; si las tareas nuevas no pasan el health check, vuelve solo a la versión anterior.

## 3. Diagnóstico

```powershell
aws logs tail "/ecs/raf-dev-server" --follow
aws ecs describe-services --cluster raf-dev --services server --query "services[0].events[:5]"
$tg = aws cloudformation describe-stacks --stack-name $stack `
  --query "Stacks[0].Outputs[?OutputKey=='TargetGroupArn'].OutputValue" --output text
aws elbv2 describe-target-health --target-group-arn $tg
```

Si las tareas no arrancan con `CannotPullContainerError` o `ResourceInitializationError`, las subredes de `ServiceSubnetIds` no tienen salida a ECR o Secrets Manager (falta NAT o VPC endpoints).

## 4. Borrar todo

```powershell
aws ecr batch-delete-image --repository-name raf-dev-server `
  --image-ids (aws ecr list-images --repository-name raf-dev-server --query "imageIds[].{imageTag:imageTag}" --output json | ConvertFrom-Json | ForEach-Object { "imageTag=$($_.imageTag)" })
aws cloudformation delete-stack --stack-name $stack
```

Borrar el stack quita la regla y el target group del ALB, pero no toca el ALB. RDS deja un snapshot final.

## Pendiente para próximos planes

- **Tiempo real** con SignalR por el ALB (Plan 05); no requiere recursos nuevos en AWS.
- **KMS** para cifrar las variables secretas (Plan 05).
- Mover las migraciones a un paso del despliegue cuando haya más de una tarea.
