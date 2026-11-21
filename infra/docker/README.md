# Imagen del servidor en Docker Hub

La imagen de `Raf.Server` se publica en Docker Hub para que cualquiera pueda levantar su propio servidor RAF sin compilarlo. Es la misma imagen que corre en AWS y en Railway, construida desde [`src/Raf.Server/Dockerfile`](../../src/Raf.Server/Dockerfile).

## Publicación

La publica el workflow [server-image.yml](../../.github/workflows/server-image.yml) con el mismo tag que el cliente:

```bash
git tag v0.3.0
git push origin v0.3.0
```

| Tag de git | Tags de la imagen |
|---|---|
| `v1.2.3` | `1.2.3`, `1.2`, `latest` |
| `v1.2.3-beta.1` | `1.2.3-beta.1` (no mueve `1.2` ni `latest`) |

- **Arquitecturas:** `linux/amd64` y `linux/arm64` en una sola imagen multiplataforma. Docker baja la que corresponde.
- **Versión:** el ensamblado lleva la del tag (`-p:Version`), igual que el cliente.
- **Una versión que ya existía:** en GitHub, *Actions → Imagen del servidor → Run workflow*, elegir el tag en *Use workflow from*. Corrido desde una rama, el workflow falla a propósito.

### Configuración del repositorio (una vez)

Hasta que estén los tres valores, el workflow avisa y no publica nada.

1. En Docker Hub, crear el repositorio (por ejemplo `<usuario>/raf-server`, público) y un *Personal access token* con permiso *Read & Write*.
2. En GitHub, *Settings → Secrets and variables → Actions*:
   - Variable `DOCKERHUB_REPOSITORY`: `<usuario>/raf-server`.
   - Secreto `DOCKERHUB_USERNAME`: el usuario de Docker Hub.
   - Secreto `DOCKERHUB_TOKEN`: el token. Nunca la contraseña de la cuenta.

Ningún valor de estos va al repo.

## Levantar un servidor con la imagen

[`compose.yml`](compose.yml) levanta Postgres, migra la base y arranca el servidor. Requiere Docker con Compose.

1. Copiar `compose.yml` a una carpeta y crear al lado un `.env` (no se versiona):

   ```
   RAF_IMAGE=<usuario>/raf-server:0.3.0
   RAF_DB_PASSWORD=<clave de la base>
   RAF_JWT_SIGNING_KEY=<64 caracteres hex al azar>
   RAF_CONFIDENTIAL_KEY=<32 bytes al azar en base64>
   RAF_ADMIN_EMAIL=<tu-email>
   RAF_ADMIN_PASSWORD=<clave inicial del admin>
   # Opcionales:
   # RAF_PORT=8080
   # RAF_MIN_CLIENT_VERSION=0.3.0
   # RAF_WEB_ORIGINS=https://<sitio-del-cliente-web>
   ```

   Para generar las claves:

   ```bash
   openssl rand -hex 32      # RAF_JWT_SIGNING_KEY
   openssl rand -base64 32   # RAF_CONFIDENTIAL_KEY
   openssl rand -hex 16      # RAF_DB_PASSWORD
   ```

   La clave inicial del admin necesita mayúscula, minúscula y número (por ejemplo `Aa1` seguido de `openssl rand -hex 12`). El servidor pide cambiarla en el primer login.

2. Levantar:

   ```bash
   docker compose --env-file .env up -d
   curl http://localhost:8080/health    # Healthy
   ```

3. En la app, conectar a la URL del servidor e iniciar sesión con `RAF_ADMIN_EMAIL` y `RAF_ADMIN_PASSWORD`.

**Guardar `RAF_CONFIDENTIAL_KEY` fuera del equipo** (por ejemplo, en un gestor de contraseñas). Es la clave maestra que cifra las variables secretas de los workspaces: si se pierde, esas variables no se recuperan.

### Cómo está armado

- **Migraciones:** el servicio `migrate` corre `--migrate` con la misma imagen y termina. `server` arranca solo si eso salió bien, con `Database__MigrateOnStartup=false`.
- **HTTPS:** el contenedor atiende HTTP en el puerto 8080. Fuera de una red local, ponerlo detrás de un proxy con TLS (Caddy, nginx, Traefik) que también deje pasar el WebSocket de `/hubs/changes`. Si el proxy lo publica bajo un subpath, indicarlo con `PathBase`.
- **Cifrado de secretos:** sin KMS, la clave maestra es `Confidential__LocalKey`, y el servidor la acepta en producción porque el compose agrega `Confidential__AllowLocalKeyInProduction=true` (igual que en Railway).
- **Cliente web:** para que un sitio del cliente web use este servidor, agregar su origen en `RAF_WEB_ORIGINS` (separados por comas).

### Actualizar a una versión nueva

Cambiar `RAF_IMAGE` en el `.env` y correr de nuevo `docker compose --env-file .env up -d`. Compose baja la imagen, migra y reemplaza el servidor. Durante el cambio hay un corte corto: este compose usa una sola instancia.

Conviene fijar la versión (`0.3.0`) en lugar de `latest`, para que una actualización no llegue sin aviso.
