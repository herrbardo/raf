# Tipo
FEATURE

# Descripción
Desplegar el backend en Railway, como alternativa a AWS (Ticket-004), con la infraestructura definida en código dentro del repo y desplegada con la CLI de Railway (ya instalada y con la cuenta configurada en el equipo del usuario).

Alcance:
- Servicio del servidor construido desde src/Raf.Server/Dockerfile y una base PostgreSQL de Railway.
- Configuración como código (railway.json o railway.toml) y un script o guía de despliegue, al estilo de infra/deploy.ps1 e infra/README.md.
- Migraciones antes de que la versión nueva atienda (dotnet Raf.Server.dll --migrate), sin correrlas desde cada instancia.
- Secretos (clave JWT, contraseña inicial del admin, credenciales de la base) como variables de Railway, nunca en el repo.
- Health check en /health, WebSocket de /hubs/changes funcionando y PathBase según el dominio que asigne Railway.

Cifrado de secretos (decidido con el usuario el 10/11/2026): no se mezcla con AWS, así que Railway no usa KMS. La clave maestra va en una variable sellada (sealed) de Railway, que el servicio recibe pero el panel, la CLI y la API no muestran. Hoy el servidor rechaza el proveedor 'local' en producción (LocalKeyWrapper.AddConfidential), así que hay que:
- Renombrarlo a un nombre que no sugiera "solo desarrollo" (por ejemplo 'env'), aceptando 'local' como alias para no romper los datos ni la configuración existentes (la columna WorkspaceKeys.Provider guarda el nombre).
- Permitirlo en producción solo con una opción explícita (por ejemplo Confidential:AllowEnvKeyInProduction=true); sin ella el servidor sigue negándose a arrancar.
- Documentar cómo generar la clave (32 bytes en base64), cargarla como variable sellada y guardar una copia de respaldo fuera de Railway: si se pierde, los secretos no se recuperan.
- Agregar tests del arranque en producción con y sin la opción.
Para el cliente no cambia nada: el cifrado es todo del servidor, y un servidor en AWS con KMS y otro en Railway se ven iguales.
Descartados: AWS KMS (mezcla infraestructuras), OpenBao/Vault Transit en Railway (mucha operación para la protección que suma dentro del mismo proyecto), KMS de Google o Azure y cifrado en el cliente.

Validar lo mismo que en el Ticket-004: dos clientes ven los cambios en tiempo real sin refrescar, el cliente se recupera de un corte, una versión nueva se despliega con la migración aplicada antes y los secretos quedan cifrados en la base.

Recordatorio: ningún identificador del proyecto de Railway (IDs, dominios, tokens) va al repo (CLAUDE.md).

# Estado
CREADO
