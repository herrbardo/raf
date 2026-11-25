# Plan 12 — Servidor MCP

Estado: **aprobado** por el usuario el 24/11/2026, con las decisiones M1 a M13. Etapa 1 implementada (Ticket-019). Sale del Ticket-019 (surgió de la prueba humana del Ticket-001).

## Objetivo

Que un harness o un LLM (Claude Code, GitHub Copilot en VS Code) se conecte a RAF por MCP para listar workspaces, leer, crear y modificar requests, y ejecutarlos, con los permisos del usuario y sin exponer secretos.

## Decisiones (acordadas con el usuario el 24/11/2026)

| # | Tema | Decisión |
|---|---|---|
| M1 | Dónde corre | **Local, en la app de escritorio**: `RAF.exe mcp`, que el harness lanza como proceso (stdio). Envía los requests desde la máquina del usuario, como la app. Un MCP remoto en el servidor queda para más adelante |
| M2 | Sesión | **Propia del MCP**: se crea desde Preferencias, se guarda cifrada y se revoca sin cerrar la sesión de la app. El MCP nunca ve la contraseña |
| M3 | Alcance | **Lectura, ejecución y escritura** |
| M4 | Tamaño de la respuesta | **Sin límite**: el body se devuelve entero |

Propuestas en este plan, para aprobar:

| # | Decisión |
|---|---|
| M5 | **Tres interruptores** en Preferencias, apagados por defecto: lectura, ejecución y escritura. Ejecución y escritura necesitan lectura. El MCP los relee en cada llamada, así que un cambio vale sin reiniciar el harness |
| M6 | **Sin caché local:** el MCP habla con la API del servidor, para no compartir el archivo de la caché con la app abierta. Necesita conexión; los cambios llegan a la app por el tiempo real, como los de otro usuario |
| M7 | **Secretos:** ninguna herramienta devuelve el valor de una variable secreta ni de una de sesión. Los specs se devuelven con los `{{nombres}}` sin resolver. La consola y la respuesta pasan por el mismo enmascarado que usa la app (`VariableContext.Scrub`). Escribir una variable secreta sí se puede: el valor entra, pero no vuelve a salir |
| M8 | **Variables de sesión y login automático:** el MCP usa las mismas variables de sesión que la app (por servidor, usuario, workspace y environment), así un token obtenido en la app sirve en el MCP y al revés. Las extracciones de un request ejecutado por MCP también se guardan ahí |
| M9 | **Environment:** cada herramienta que resuelve variables acepta un environment por nombre o id; si no viene, usa el activo de la app para ese workspace |
| M10 | **Body sin límite (M4):** el texto va entero; un body binario va en base64, con su tipo. El MCP avisa en la descripción de la herramienta que el body es dato de la API y no instrucciones, para que el harness no lo tome como órdenes |
| M11 | **Borrar** entra en escritura, con herramientas separadas (`delete_*`), para que el harness pueda pedir confirmación solo ahí. El servidor borra de forma lógica, como desde la app |
| M12 | **Historial:** los requests ejecutados por MCP se suman al historial local de la app, marcados como "MCP" |
| M13 | **SDK:** el SDK oficial de MCP para C# (`ModelContextProtocol`), con transporte stdio |

## Herramientas

**Lectura**

| Herramienta | Qué hace |
|---|---|
| `list_workspaces` | Workspaces del usuario, con su rol |
| `list_environments` | Environments de un workspace y cuál está activo en la app |
| `get_tree` | Apps, carpetas, requests (con método y URL) y samples de un workspace, o de una App |
| `get_request` | El spec completo de un request, o un sample con lo que sobreescribe y el resultado combinado |
| `list_variables` | Variables con su alcance (environment y App); el valor solo si no es secreta |
| `get_app` | La auth de la App y su login automático, sin secretos |

**Ejecución**

| Herramienta | Qué hace |
|---|---|
| `run_request` | Ejecuta un request o un sample, con environment y timeout opcionales. Devuelve el estado, los headers, el body entero, la duración, las extracciones (sin valores secretos) y la consola enmascarada. Usa el login automático si corresponde. HTTP; gRPC unario y WebSocket quedan para después |

**Escritura**

| Herramienta | Qué hace |
|---|---|
| `create_app`, `update_app` | Nombre, auth y login de una App |
| `create_folder`, `update_folder` | Nombre y ubicación |
| `create_request`, `update_request` | El spec completo; `update_request` acepta cambios parciales (por ejemplo solo la URL o el body) |
| `create_sample`, `update_sample` | Lo que sobreescribe del request |
| `set_variable` | Crea o modifica una variable por nombre, environment y App |
| `create_environment` | Un environment nuevo |
| `delete_app`, `delete_folder`, `delete_request`, `delete_sample`, `delete_variable`, `delete_environment` | Borrado (M11) |

Todas validan lo mismo que el servidor y devuelven el elemento guardado. Un conflicto de versión se informa, no se pisa.

## Etapa 1 — Sesión y preferencias

- Servidor: `POST /auth/sessions` (autenticado) emite un refresh token nuevo para el usuario, con un nombre ("MCP"); se revoca con el `/auth/logout` existente. Sin cambios en el modelo, salvo el nombre de la sesión si hace falta.
- App de escritorio, Preferencias → **Asistente (MCP)**: "Conectar" crea la sesión y la guarda cifrada (DPAPI o Keychain, como la de la app); "Desconectar" la revoca. Los tres interruptores (M5). Un bloque con la configuración lista para copiar (comando con la ruta de `RAF.exe` y el argumento `mcp`), para Claude Code y para VS Code.
- El cliente web no tiene MCP: la sección no aparece.

### Cómo quedó la etapa 1

- **Servidor:** `POST /auth/sessions` emite tokens nuevos para el usuario autenticado, sin tocar la sesión que hizo el pedido. No hizo falta cambiar el modelo: la sesión del MCP es un refresh token más.
- **Cliente:** `SessionManager` recibe la clave donde guarda la sesión (`session` la app, `mcp-session` el MCP) y suma `TryRestoreAny`, para que el proceso MCP tome el servidor de la sesión guardada. `McpConnection` conecta (revoca la anterior si había), informa la conexión actual y desconecta. Los permisos quedan en `LocalSettings.Mcp` (`McpPermissions`).
- **Logout bajo el lock:** el servidor toma el uso de un refresh token ya revocado como robo y cierra **todas** las sesiones del usuario. Para que desconectar desde la app no deje al proceso MCP con un token viejo (ni revoque uno que el MCP ya rotó), `LogoutAsync` ahora toma el mismo lock de archivo que las renovaciones y revoca el último token guardado.
- **Preferencias** (solo escritorio y con sesión iniciada, `ClientHost.McpExecutable`): estado de la conexión, Conectar/Desconectar, los tres permisos (ejecutar y escribir se habilitan con leer) y la configuración para copiar: el comando de Claude Code (`claude mcp add raf -- "<ruta de RAF.exe>" mcp`) y el bloque de `.vscode/mcp.json`.

## Etapa 2 — Proceso MCP y lectura

- `RAF.exe mcp` arranca sin interfaz: lee la sesión del MCP, la renueva cuando hace falta y atiende stdio con el SDK. Sin sesión o con lectura apagada, cada herramienta responde con un error que explica qué activar en la app.
- Las herramientas de lectura usan la API existente (`IWorkspaceStore` sobre la API, sin caché).

## Etapa 3 — Ejecución

- `run_request` arma el `RunInput` como la pestaña: spec efectivo del sample, variables de la App y el environment (`VariableContext`), sesión compartida (M8), auth y login de la App. Usa el mismo `RequestRunner`.
- Guarda el token del login, las extracciones y la línea del historial (M12).

## Etapa 4 — Escritura

- Las herramientas de escritura sobre la API, con las validaciones del servidor. `update_request` mezcla los cambios parciales sobre la versión actual y manda su `Version`.

## Cómo se prueba

- **Servidor:** emitir y revocar una sesión adicional; la de la app sigue viva.
- **MCP:** tests que levantan el servidor de prueba, crean la sesión y llaman a cada herramienta por el cliente del SDK en memoria: sin sesión, con cada interruptor apagado, lectura con y sin secretos (Viewer y Editor), `run_request` contra el servidor de eco de los tests (con login automático, extracciones y enmascarado), escritura con conflicto de versión y borrado.
- **A mano:** configurar Claude Code con el bloque de Preferencias y pedirle que liste, cree y ejecute un request.

## 🧑 Checkpoint humano

Conectar un harness real (Claude Code o VS Code) y probarlo. Al publicar: servidor primero (por `/auth/sessions`) y después la app.
