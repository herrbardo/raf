# Despliegue en Railway

Alternativa al despliegue en AWS ([infra/README.md](../README.md)), sin piezas de AWS. Para el cliente es igual: se conecta a la URL del servidor y no sabe dónde corre.

```
Cliente ──HTTPS / WebSocket──> dominio de Railway ──> servicio "server" (Dockerfile) ──red privada──> Postgres de Railway
Navegador ──HTTPS──> dominio de Railway ──> servicio "web" (Caddy, archivos estáticos)
          └── el sitio llama a la API del servidor (CORS) y envía los requests por su relay
```

- **Infraestructura como código:** [`.railway/railway.ts`](../../.railway/railway.ts) define la base y los servicios, con su build, el health check en `/health`, las migraciones antes de cada versión y las variables que no son secretas. Se aplica con `railway config apply`.
- **Cliente web:** el servicio `web` se construye con [`src/Raf.Client.Browser/Dockerfile`](../../src/Raf.Client.Browser/Dockerfile) (compila a WebAssembly) y lo sirve Caddy. El servidor le habilita CORS con `Cors__AllowedOrigins__0`, una referencia al dominio público de `web`: el dominio no queda escrito en el repo. El sitio no trae la URL del servidor: la indica cada usuario, y puede conectarse a otro servidor RAF si ese servidor agrega el dominio del sitio a sus orígenes.
- **Relay:** los requests que envía la web los ejecuta el servidor (`POST /relay`), con sesión, sin alcanzar direcciones internas (localhost, la red privada de Railway, la metadata de la nube) y con límites de tamaño, tiempo y cantidad por usuario (`Relay__*`, ver `appsettings.json`).
- **Secretos:** no están en el repo. El archivo los declara con `preserve()` (Railway conserva el valor que ya tiene) y [`deploy.ps1`](deploy.ps1) los genera la primera vez.
- **Migraciones:** el servicio arranca con `Database__MigrateOnStartup=false`. Railway corre `dotnet Raf.Server.dll --migrate` (pre-deploy) con la imagen nueva antes de pasarle el tráfico. Si falla, la versión anterior sigue atendiendo.
- **Tiempo real:** el WebSocket de `/hubs/changes` va por el mismo dominio. Con una sola réplica no hace falta nada más; con varias, los avisos se reparten con `LISTEN/NOTIFY` de Postgres, igual que en AWS.

## Cifrado de las variables secretas

Railway no tiene KMS, así que la clave maestra que protege las variables secretas de los workspaces es la variable `Confidential__LocalKey` (32 bytes en base64), **sellada**: el servicio la recibe, pero el panel, la CLI y la API ya no la muestran. El servidor acepta este proveedor en producción solo porque el servicio tiene `Confidential__AllowLocalKeyInProduction=true`.

**Si se pierde la clave, las variables secretas guardadas no se recuperan.** Antes de sellarla hay que guardar una copia fuera de Railway, por ejemplo en un gestor de contraseñas.

## 1. Primer despliegue

Requisitos: [CLI de Railway](https://docs.railway.com/cli) con sesión (`railway login`), Node.js (la CLI lo usa para leer `railway.ts`) y git.

```powershell
railway init --name raf                    # o crear el proyecto desde el panel
railway link                               # vincula este directorio al proyecto y al entorno (queda fuera del repo)
npm install --prefix .railway              # SDK con el que la CLI lee railway.ts (deploy.ps1 lo instala si falta)
$env:_ = "$env:APPDATA\npm\node_modules\@railway\cli\bin\railway.exe"   # solo Windows, ver nota abajo
railway config plan                        # muestra qué va a crear, sin cambiar nada
.\infra\railway\deploy.ps1 -AdminEmail <tu-email>
```

**Nota para Windows:** el SDK controla la versión de la CLI ejecutando `railway` sin shell, y no encuentra el `railway.cmd` que instala npm. Falla con "requires Railway CLI 5.42.1 or newer" aunque la CLI sea más nueva. La variable `_` le indica el ejecutable real. `deploy.ps1` ya la define.

El script:

1. Aplica `.railway/railway.ts` (crea la base y el servicio; pide confirmación salvo con `-Yes`).
2. Genera las variables que falten: `Confidential__LocalKey`, `Jwt__SigningKey` y `Bootstrap__AdminPassword`, y carga `Bootstrap__AdminEmail`. Los valores van por stdin y no se imprimen.
3. Sube el código con `railway up`. Railway construye la imagen, migra, crea el admin y, cuando `/health` responde, pasa el tráfico.
4. Genera el dominio público (`*.up.railway.app`) si el servicio todavía no tiene uno.

**Después del primer despliegue:**

```powershell
# Copiar la clave maestra y la contraseña inicial del admin (solo se pueden leer antes de sellarlas):
railway variable list --service server --kv | Select-String 'Confidential__LocalKey|Bootstrap__AdminPassword'
```

1. Guardar `Confidential__LocalKey` fuera de Railway.
2. En el panel, en *server → Variables*, sellar `Confidential__LocalKey` y `Jwt__SigningKey` (menú de cada variable → *Seal*). `Bootstrap__AdminPassword` también se puede sellar: el servidor pide cambiarla en el primer login.
3. Ver el dominio con `railway domain list --service server` y comprobar `https://<dominio>/health` → `Healthy`.
4. En la app: conectar a `https://<dominio>` e iniciar sesión con el email del admin y esa contraseña.
5. Cliente web: abrir el dominio de `railway domain list --service web`, conectar al dominio del servidor e iniciar sesión igual.

## 2. Desplegar una versión nueva

```powershell
.\infra\railway\deploy.ps1              # servidor y después el sitio
.\infra\railway\deploy.ps1 -Only web    # solo el sitio (o -Only server)
```

El sitio se publica con un nombre de archivo por versión para lo pesado (`_framework/*.wasm`), así que el navegador no mezcla versiones: al recargar toma la nueva. Una pestaña abierta con una versión vieja recibe 426 si el servidor sube `Clients:MinVersion`, y ofrece recargar.

Los secretos ya existen y no se tocan. Railway hace el cambio sin corte: la versión nueva recibe tráfico recién cuando pasa el health check. Como en AWS, las migraciones tienen que ser compatibles con la versión anterior, que sigue atendiendo mientras arranca la nueva.

## 3. Diagnóstico

```powershell
railway logs --service server              # logs del servidor
railway logs --service server --build      # logs del build
railway status
railway connect postgres                   # psql contra la base
```

Para comprobar que los secretos quedan cifrados: en `railway connect postgres`, toda fila de `"Variables"` con `"IsSecret" = true` tiene un `"Value"` que empieza con `enc:v1:`.

## 4. Borrar todo

Desde el panel (*Settings → Danger → Delete project*) o con `railway delete`. Se borra también la base, sin snapshot: exportarla antes si hace falta (`pg_dump` contra `DATABASE_PUBLIC_URL`).
