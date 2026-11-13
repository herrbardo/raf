# Plan 09 — Cliente web

Estado: **🧪 implementado, falta la prueba humana** (desplegar en Railway y recorrerlo).

## Cómo quedó (decisiones de implementación)

Lo que cambió respecto de lo planificado más abajo:

| Tema | Plan | Implementado |
|---|---|---|
| Envío por el relay | Interfaz `IRequestSender` | `RequestRunner` recibe un `HttpMessageHandler` por constructor. En web es `RelayHandler` (Core), que convierte cada salto en `POST /relay` y arma la respuesta y la traza de conexión con los tiempos del servidor. `RequestRunner` no cambió por dentro: redirecciones, login de la App, reintento ante 401 y consola son los mismos |
| Locks entre instancias | `IInstanceLock` | Sin abstracción: en WebAssembly los archivos viven en un sistema de archivos en memoria (`AppPaths.DataDirectory` = `/raf`) y los locks de archivo no bloquean (hay un solo proceso por pestaña) |
| Historial | `sessionStorage` | Archivos en el sistema de archivos en memoria: dura lo que la pestaña abierta, igual que la caché. No sobrevive a recargar |
| Preferencias | `ILocalStore` | `SettingsStore` acepta un archivo o un `ITextStore` (`localStorage` en web). `ISecureStore` hereda de `ITextStore` |
| Cifrado de la caché | Igual que desktop | `AesGcm` no existe en el navegador: la caché web no cifra los secretos (`ClientHost.EncryptCache`), ya que vive en memoria |
| Ventanas | Vista + envoltorio por ventana | Cada ventana secundaria es un `DialogView` (`XxxDialog.axaml`). `IWindowService` lo muestra en una `Window` (desktop, `WindowService`) o superpuesto en la página (web, `OverlayWindowService`). Los diálogos genéricos (`Dialogs`) usan el mismo mecanismo, así hay una sola implementación |
| Consola de eventos en web | Panel lateral | Diálogo superpuesto, que se abre y se cierra igual (`Ctrl+Shift+L`, `Esc`) |
| Fuente monoespaciada | Embeber Cascadia Mono | No hizo falta: el navegador tiene una monoespaciada de respaldo y el texto se ve monoespaciado |
| Capacidades (`IPlatformInfo`) | Interfaz aparte | `ClientHost` (abstracto, en la UI compartida): cada cabeza registra sus servicios y muestra la vista raíz (`ShellView`) |
| Hosts internos permitidos en el relay | `Relay:AllowPrivateNetworks` | `Relay:AllowedPrivateHosts`: lista de nombres que pueden resolver a direcciones internas. Más preciso que abrir todas las redes privadas |
| Origen CORS en desarrollo | Generado por `dev-setup.ps1` | `appsettings.Development.json` (`http://localhost:5090`, el puerto fijo del sitio en desarrollo). No es un secreto ni infraestructura |
| Origen CORS en Railway | Texto armado con el dominio | Referencia a `RAILWAY_PUBLIC_DOMAIN` del servicio `web`, que llega sin esquema: `Cors:AllowedOrigins` asume https cuando falta |

**Estructura:**

| Proyecto | Contenido |
|---|---|
| `src/Raf.Client.UI` | La interfaz compartida: `App`, ViewModels, vistas, controles, estilos, `ShellView` (la vista raíz), diálogos y `ClientHost` |
| `src/Raf.Client` | Desktop: `Program`, `DesktopHost`, `MainWindow` (envuelve a `ShellView`), Velopack |
| `src/Raf.Client.Browser` | Web: `Program`, `BrowserHost` (almacenamiento del navegador, relay, recarga), `wwwroot` (`index.html`, `main.js`), `Dockerfile` y `Caddyfile`. Fuera de `Raf.slnx`: necesita el workload `wasm-tools` |

## Objetivo

Una versión web de RAF que funcione lo más parecido posible a la de escritorio: el mismo login contra el servidor que elige el usuario, los mismos workspaces, la misma edición colaborativa en tiempo real y el mismo envío de requests. Se despliega en Railway como un servicio aparte del servidor, así que el servidor tiene que habilitar CORS para el origen del sitio.

## Decisiones (acordadas con el usuario el 13/11/2026)

| # | Tema | Decisión |
|---|---|---|
| W1 | Tecnología de la UI | **Avalonia en el navegador** (`Avalonia.Browser`, WebAssembly). Reutiliza las vistas AXAML, los ViewModels, los estilos y `Raf.Client.Core` tal cual. Blazor obligaría a reescribir toda la UI y a mantener dos |
| W2 | Envío de requests | **Relay en el servidor.** El cliente web arma el request como desktop (sample, herencia de auth, variables, login de la App) y se lo pasa al servidor RAF, que lo envía y devuelve la respuesta. Desde el navegador no se podría: el CORS de las APIs de terceros, los headers prohibidos (`Cookie`, `Host`, `User-Agent`) y los certificados inválidos lo impiden |
| W3 | Sesión y variables de sesión | **Hasta cerrar la pestaña:** `sessionStorage`. Sobreviven a recargar la página y se pierden al cerrar la pestaña. Las preferencias no sensibles (idioma, servidores recientes, environment activo) van en `localStorage` |
| W4 | Caché y edición sin conexión | **Online, caché por pestaña:** el mismo `SyncedWorkspaceStore` y `SyncEngine` sobre SQLite **en memoria**. Al entrar se descarga todo; edición, outbox y conflictos funcionan igual mientras la pestaña esté abierta. Si se cierra con cambios sin enviar, el navegador pregunta antes de salir |
| W5 | Hosting | **Servicio `web` aparte en Railway**, archivos estáticos. El servidor habilita CORS para su origen. La URL del servidor no va compilada en el sitio: la indica el usuario, como en desktop |

## Qué se comparte y qué cambia

### Se comparte sin cambios

| Pieza | Dónde |
|---|---|
| Dominio: tipos de request, merge de samples, resolución de variables | `Raf.Domain` |
| Contratos con el servidor | `Raf.Contracts` |
| API, sesión y renovación de tokens, versión del cliente (426) | `Raf.Client.Core/Api`, `Session` |
| Sincronización, outbox, conflictos, tiempo real (SignalR por WebSocket, el token ya viaja por query string) | `Raf.Client.Core/Sync` |
| Armado y ejecución lógica del request: herencia, login automático de la App, reintento ante 401, redirecciones, extracciones, consola del request | `Raf.Client.Core/Execution` |
| Formato y búsqueda en la respuesta, autocompletar de variables, permisos, textos en es/en | `Raf.Client.Core` |
| ViewModels, vistas AXAML, controles, estilos y tema | hoy en `Raf.Client`, pasan a `Raf.Client.UI` |

### Se abstrae (una interfaz en Core o en la UI compartida, una implementación por plataforma)

| Abstracción | Desktop | Web |
|---|---|---|
| `IRequestSender`: el envío de un salto HTTP (sin seguir redirecciones; las sigue `RequestRunner`, como hoy) | `SocketsHttpHandler` directo, con `RunTrace.ConnectAsync` para DNS/TCP/TLS | `RelayRequestSender`: `POST /relay` al servidor RAF; la traza de conexión viene en la respuesta |
| `ISecureStore` (ya existe) | DPAPI / Keychain | `sessionStorage` (W3) |
| `ILocalStore` para preferencias e historial (`SettingsStore`, `RunHistoryStore`) | Archivos en `AppPaths.DataDirectory` | `localStorage` (preferencias) y `sessionStorage` (historial, porque guarda URLs y respuestas) |
| `IInstanceLock` (hoy, archivos `session.lock` y el lock de `SyncEngine`) | Lock de archivo entre instancias | Lock en proceso; cada pestaña tiene su propia sesión (ver riesgos) |
| Apertura de la caché | Archivo SQLite cifrado | SQLite en memoria (W4) |
| Body grande de la respuesta (hoy desborda a un archivo temporal) | Archivo temporal | Tope en memoria; el servidor ya lo corta en el relay |
| `IUpdateService` | Velopack | No busca versiones: un 426 muestra "Recargar la página" |
| `IDialogs` (ya existe) | `WindowDialogs` | Diálogos superpuestos dentro de la página |
| `IWindowService`: Environments, Conflictos, Workspace, Admin, Auth de la App, Preferencias, Acerca de, Consola de eventos | `Window` + `ShowDialog` | Panel superpuesto (modal) en la vista principal |
| Guardar archivo (respuesta) | Diálogo del sistema | `StorageProvider` del navegador o descarga |
| `IPlatformInfo`: capacidades (`CanUpdate`, `HasWindows`, `CanReachLocalNetwork`), para ocultar opciones que no aplican | — | — |

### Cambia directamente por el tipo de interfaz

- **Arranque:** `Program`/`App` de desktop usan `IClassicDesktopStyleApplicationLifetime` y `MainWindow`; el web usa `StartBrowserAppAsync` con `ISingleViewApplicationLifetime` y una vista raíz. La composición de servicios (`BuildServices`) se comparte y cada cabeza registra sus implementaciones.
- **Ventanas:** en el navegador no hay ventanas. Cada `XxxWindow.axaml` se divide en una vista (`XxxView`, compartida) y un envoltorio: `Window` en desktop, panel modal en web.
- **Consola de eventos:** en desktop es una ventana no modal; en web, un panel lateral o inferior que se abre y cierra.
- **Atajos:** `Ctrl+S`, `Ctrl+Shift+L` y otros los toma el navegador. Se verifican uno por uno y, si el navegador no los cede, se usan alternativas y se documentan.
- **Fuentes:** el navegador no tiene las del sistema. `RafMonoFont` (`Cascadia Mono, Consolas, Menlo`) no existe ahí: se embebe una monoespaciada con licencia libre (Cascadia Mono, OFL) en la UI compartida, para que desktop y web se vean igual.
- **Idioma:** el del sistema sale de `navigator.language`. Los recursos `.en` tienen que viajar en el bundle (`SatelliteResourceLanguages`).
- **Banner de actualización y `UpdateRequiredView`:** en web, "Hay una versión nueva — Recargar".
- **Certificados inválidos y opciones locales del request:** se mantienen; el relay las aplica en el servidor.

## Etapa 0 — Prueba técnica (de esto depende el resto)

Antes de mover código, un proyecto de prueba descartable con `Avalonia.Browser` (misma versión que desktop) que confirme:

1. EF Core + `Microsoft.EntityFrameworkCore.Sqlite` en WebAssembly con SQLite en memoria (requiere el workload `wasm-tools` para enlazar `e_sqlite3`), con el trimming que necesite EF.
2. SignalR por WebSocket con `SkipNegotiation`, contra el servidor local.
3. `AvaloniaEdit` (editor del body y la ventana de autocompletar) y popups en modo single view.
4. Peso del bundle y tiempo de carga (con y sin AOT), y que los atajos de teclado lleguen a la app.

**Si EF Core no funciona en WebAssembly**, `SyncedWorkspaceStore` pasa a depender de un repositorio con dos implementaciones (EF/SQLite en desktop, en memoria en web) y este plan suma esa etapa. Se registra el resultado en este documento antes de seguir.

### Resultado (13/11/2026)

Prueba con `Avalonia.Browser` 11.3.22 sobre .NET 10, referenciando `Raf.Client.Core` sin cambios y llamando al `SyncedWorkspaceStore` real:

| Punto | Resultado |
|---|---|
| EF Core + SQLite | ✅ Funciona sin la alternativa en memoria: crea la caché, escribe y lee. Requiere el workload `wasm-tools` y `WasmBuildNative`. La ruta en el sistema de archivos virtual (MEMFS) ya es "en memoria": se pierde al recargar |
| Trimming | ⚠️ Recortar EF Core lo rompe (`MissingMethodException` al armar el modelo). Se resuelve con `TrimmerRootAssembly` para `Microsoft.EntityFrameworkCore`, `.Relational`, `.Sqlite` y los ensamblados propios (`Raf.Client.Core`, `Raf.Contracts`, `Raf.Domain`) |
| JSON | ⚠️ El SDK de WebAssembly apaga la serialización por reflexión. Se habilita con `JsonSerializerIsReflectionEnabledByDefault=true` (el código usa `ClientJson.Options` sin contexto generado) |
| Cifrado de la caché | ❌ `AesGcm` no existe en el navegador (`PlatformNotSupportedException`), y `SecretCipher` lo usa para las variables secretas. Como la caché web vive en memoria (W4), en web no se cifra: `SecretCipher` pasa a ser una interfaz con una implementación que no cifra (etapa 2). `SHA256` y `RandomNumberGenerator` sí funcionan |
| Idioma | ⚠️ Los recursos `.en` no se cargan solos. Se resuelve en `main.js` con `withConfig({ loadAllSatelliteResources: true })`. `CultureInfo.CurrentUICulture` arranca con el idioma del navegador |
| AvaloniaEdit y autocompletar | ✅ El editor y el popup de `CompletionWindow` funcionan en single view. Sus estilos tienen que incluirse desde AXAML (desde código, el trimming los elimina), como ya hace la app |
| Atajos | ✅ `Ctrl+S` llega a la app y el navegador no abre "Guardar página" |
| SignalR | ✅ El cliente arranca y abre el WebSocket desde WebAssembly (con un token inválido, el servidor lo rechaza como corresponde). La conexión con un token real se valida en la etapa 4, cuando el servidor ya tenga CORS para hacer el login |
| Peso | 8,2 MB con Brotli, sin AOT (el `dotnet.native.wasm` con SQLite y Skia son 2,8 MB). Aceptable: **sin AOT** por ahora |

## Etapa 1 — Servidor: CORS y relay

**CORS**

- Configuración `Cors:AllowedOrigins` (lista; vacía = sin CORS, como hoy). Sin comodines en producción.
- Política: métodos `GET, POST, PUT, PATCH, DELETE`; headers `Authorization`, `Content-Type` y `X-Raf-Client-Version` (`RafHeaders.ClientVersion`); sin credenciales (el JWT va en el header, no hay cookies); `Access-Control-Max-Age` alto para no repetir el preflight.
- `UseCors` va **antes** de `UseClientCompatibility`, de la autenticación y del manejo de errores: un 426, un 401 o un problem+json sin headers de CORS no se pueden leer desde el navegador, y la app mostraría "no se puede conectar" en lugar del error real.
- El WebSocket de `/hubs/changes` no pasa por CORS: se valida el header `Origin` contra la misma lista (si viene) para que otro sitio no lo use con un token robado.
- Tests: preflight permitido y rechazado, 426 y 401 con headers de CORS, origen no listado sin headers, WebSocket con `Origin` ajeno rechazado.

**Relay (`POST /relay`)**

- Contrato en `Raf.Contracts`: método, URL, headers, body (base64), timeout y aceptar certificados inválidos; responde status, reason, headers, body (base64, con tope), si se truncó y la traza de conexión (DNS, conexión, TLS, IP remota), en el formato de `RunTrace`.
- Un salto por llamada, sin seguir redirecciones ni usar cookies, igual que `RequestRunner` hoy: así la consola del request muestra cada redirección igual que en desktop.
- Requiere sesión válida (política por defecto). No guarda ni registra headers ni bodies: pueden traer secretos. El log solo deja método, host y status.
- **Protección SSRF:** se resuelve el DNS en el servidor y se rechaza cualquier IP de loopback, privada, link-local (incluido `169.254.169.254`), de la red interna de Railway (`*.railway.internal`) y multicast, también tras cada redirección (el cliente pide cada salto). Se conecta a la IP ya validada para que no cambie entre la validación y la conexión. `Relay:AllowPrivateNetworks` lo permite para un servidor propio en una red interna (por defecto `false`).
- Límites configurables: `Relay:MaxRequestBytes` (10 MB), `Relay:MaxResponseBytes` (10 MB, el resto se descarta y se avisa), `Relay:MaxTimeoutSeconds` (120) y rate limit por usuario (`Relay:PerMinute`, 120).
- `Relay:Enabled` (por defecto `true`). Si está apagado, el cliente web lo informa al enviar.
- Tests: auth obligatoria, envío y respuesta, redirección no seguida, rechazo de IPs privadas y de un DNS que apunta a una, tope de body, timeout, rate limit.

## Etapa 2 — Core: abstracciones de plataforma

- Introducir las interfaces de la tabla anterior en `Raf.Client.Core` y mover a ellas las implementaciones actuales sin cambiar el comportamiento de desktop.
- `RequestRunner` recibe un `IRequestSender`; el desktop registra el actual (`SocketsHttpHandler` + `RunTrace`). `RelayRequestSender` vive en Core (solo usa `HttpClient` y los contratos) y se prueba contra el servidor de los tests.
- `SyncedWorkspaceStore.OpenAsync` acepta un origen de conexión (archivo o memoria). En memoria no hace falta cifrar la caché, pero se mantiene el mismo código.
- Revisar que no haya esperas síncronas sobre tareas (`.Wait()`, `.Result`, `GetAwaiter().GetResult()`) ni `Task.Run` que dependa de otro hilo: en WebAssembly hay un solo hilo y se cuelgan.
- Tests de Core para cada implementación nueva que no dependa del navegador (relay, caché en memoria, almacén en memoria).

## Etapa 3 — UI compartida

- Nuevo proyecto `src/Raf.Client.UI` (biblioteca Avalonia) con `ViewModels`, `Views`, `Controls`, `Styles`, los recursos y `IDialogs`/`IWindowService`. `Raf.Client` queda como cabeza de escritorio: `Program`, `App`, Velopack, `WindowDialogs` y los envoltorios `Window`.
- Las ventanas se dividen en vista + envoltorio (ver arriba). Los ViewModels no cambian: hoy reciben callbacks como `ShowAppAuth` y `ShowPreferences`; pasan a usar `IWindowService`.
- `Dispatcher.UIThread` sigue igual (funciona en web).
- Fuente monoespaciada embebida.
- **Desktop tiene que quedar igual**: se valida con los tests y una recorrida manual de cada ventana antes de seguir.

## Etapa 4 — Cabeza web

- Nuevo proyecto `src/Raf.Client.Browser` (`Microsoft.NET.Sdk.WebAssembly` + `Avalonia.Browser`): `Program` con `StartBrowserAppAsync`, `wwwroot/index.html` con una pantalla de carga, y las implementaciones web: almacenes sobre `localStorage`/`sessionStorage` (por `JSImport`), `OverlayDialogs`, `OverlayWindowService`, `IUpdateService` sin actualizaciones y `RelayRequestSender`.
- `beforeunload` avisa si hay cambios en el outbox sin enviar (W4).
- La versión del sitio es la misma que la del repo (`Directory.Build.props`) y viaja en `X-Raf-Client-Version`, así `Clients:MinVersion` también vale para la web.
- Pantalla de conexión: igual que desktop. Si el servidor no responde por CORS, el mensaje lo dice ("el servidor no acepta conexiones desde este sitio: falta agregar su origen a `Cors:AllowedOrigins`"), porque el navegador no distingue ese caso de un servidor caído.

## Etapa 5 — Despliegue en Railway

- `src/Raf.Client.Browser/Dockerfile`: build con el SDK de .NET 10 + workload `wasm-tools`, `dotnet publish -c Release`, y una imagen chica que sirve los estáticos (Caddy o nginx) con:
  - tipo `application/wasm` y los `.br`/`.gz` que genera el publish;
  - `_framework/*` con cache inmutable (llevan hash) e `index.html` sin cache, para que un deploy nuevo se vea al recargar;
  - headers de seguridad: CSP con `connect-src https: wss:` (el servidor lo elige el usuario, así que no se puede fijar un dominio), `frame-ancestors 'none'`, `X-Content-Type-Options`, `Referrer-Policy`;
  - health check en `/health`.
- `.railway/railway.ts`: servicio `web` con su Dockerfile, y en `server` la variable `Cors__AllowedOrigins__0` armada con la referencia al dominio público del servicio `web` (variable de referencia de Railway), sin escribir el dominio en el repo.
- `infra/railway/deploy.ps1`: sube los dos servicios (`railway up --service`), genera el dominio de `web` si falta y un parámetro `-Only server|web`.
- Orden al desplegar: primero el servidor (CORS y relay), después el sitio.
- CI: el workflow del cliente compila también `Raf.Client.Browser` (con el workload) para que no se rompa sin darse cuenta.
- Documentar en `infra/railway/README.md` y en el `README.md` (estructura, cómo correrlo localmente con `dotnet run --project src/Raf.Client.Browser` y qué origen poner en `Cors:AllowedOrigins` en desarrollo, que `dev-setup.ps1` agrega al `.env`/user-secrets).
- AWS (plan 05) queda fuera: `infra/raf.yaml` solo suma el parámetro opcional de orígenes CORS, sin valor por defecto.

## Riesgos y cómo se cubren

| Riesgo | Cobertura |
|---|---|
| EF Core/SQLite no corre en WebAssembly o el trimming lo rompe | Etapa 0; alternativa de repositorio en memoria |
| El relay como proxy abierto o para llegar a la red interna (SSRF) | Solo con sesión, bloqueo de IPs privadas tras resolver DNS, límites y rate limit (etapa 1) |
| Los secretos de las variables pasan por el servidor al enviar | Ya viajan por TLS al mismo servidor que los guarda cifrados; el relay no los registra ni los guarda |
| "Duplicar pestaña" copia el `sessionStorage` y dos pestañas usan el mismo refresh token: el servidor lo toma como robo y cierra todas las sesiones | Al arrancar, la pestaña marca su sesión con un id propio; si detecta una copia (vía `BroadcastChannel`), descarta los tokens copiados y pide login |
| Atajos que el navegador no cede | Verificar en la etapa 0 y documentar alternativas |
| Bundle pesado (decenas de MB) | Compresión Brotli, cache inmutable y pantalla de carga; medir en la etapa 0 y decidir AOT ahí |
| Cambios del outbox perdidos al cerrar la pestaña | Aviso de `beforeunload` (W4) |
| APIs en localhost o red local no se pueden probar desde la web | Limitación aceptada (W2): se usan desde desktop. El mensaje del relay lo explica |

## Verificación

- `dotnet build` y `dotnet test` (tests nuevos de CORS, relay y Core).
- Desktop: recorrida de todas las ventanas y envío de requests, igual que antes.
- Web local contra el servidor local: login, árbol, edición con dos clientes (uno desktop y uno web) viendo los cambios en tiempo real, conflicto, envío con redirección, login automático de una App, extracción a variable de sesión, idioma inglés, guardar respuesta, recargar la página sin perder la sesión y cerrar la pestaña perdiéndola.

## 🧑 Checkpoint humano

Desplegar en Railway con `deploy.ps1`, abrir el dominio del servicio `web`, conectar al dominio del servidor y repetir la recorrida de la verificación. Confirmar que el relay rechaza una URL interna (por ejemplo `http://localhost`).
