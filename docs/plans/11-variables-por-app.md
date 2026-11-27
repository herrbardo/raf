# Plan 11 — Variables por App

Estado: **aprobado** por el usuario el 24/11/2026, con las decisiones V1 a V10. **Implementado** (las tres etapas, Ticket-018): probado con tests y a mano en el cliente web; sin probar en la app de escritorio. Sale del Ticket-018 (surgió de la prueba humana del Ticket-001).

> **Dejado sin efecto el 26/11/2026 (Ticket-044).** A pedido del usuario, las variables vuelven a valer para todas las Apps: no hay columna ni filtro por App. La migración `SharedVariables` pasó a "Todas" las variables de una App; si chocaban con otra del mismo nombre en el mismo environment, ganó la de "Todas" (o, si no había, la modificada más recientemente) y las demás quedaron marcadas como borradas. La ventana de environments lista en cada environment las claves de todos, en orden alfabético, con las que faltan sin valor. Lo que sigue queda como registro de cómo había sido.

## Objetivo

Que una variable pueda valer para una sola App, así cada App define su propio valor para el mismo nombre (por ejemplo `{{baseUrl}}` en todas las Apps, con un valor distinto en cada una) sin prefijos como `ordersBaseUrl` y `usersBaseUrl`. De paso, la ventana de environments deja filtrar por App, para que con muchas variables se vean solo las de la App en la que se está trabajando.

## Decisiones (acordadas con el usuario el 24/11/2026)

| # | Tema | Decisión |
|---|---|---|
| V1 | Dónde se editan | En la **ventana de environments actual**, con una columna **App** cuyo valor por defecto es **"Todas"**. No hay ventana nueva |
| V2 | Nombres repetidos | Un nombre es único **por environment (o globales) y App**: en dev puede haber un `baseUrl` de Orders, otro de Users y uno de "Todas" |
| V3 | Login compartido | Usa **solo las variables de "Todas"**: lo comparten varias Apps y el token también, así que no puede depender de qué App lo disparó |
| V4 | Cliente web | **Igual que escritorio**: la interfaz es compartida |

Aprobadas con el plan, el mismo día:

| # | Decisión |
|---|---|
| V5 | **Precedencia:** sesión > environment activo de la App > environment activo de "Todas" > globales de la App > globales de "Todas". Es la de hoy (sesión > environment > globales) con una regla más: dentro de cada pestaña, la variable de la App le gana a la de "Todas". Una global de la App **no** le gana a una del environment de "Todas" |
| V6 | **Filtro por App** junto al buscador: "Todas las Apps" o una App. Si hay una App elegida, la grilla muestra las variables de esa App y las de "Todas". Al abrir la ventana, el filtro arranca en la App del request de la pestaña activa (o "Todas las Apps" si no hay ninguna) |
| V7 | **Cambiar la App** de una variable existente se puede, desde la misma columna: es moverla de alcance. Si ya hay una con ese nombre en el destino, se avisa y no se guarda |
| V8 | **Borrar una App** borra sus variables. **Duplicar una App** (Ticket-034) copia sus variables a la copia. Mover un request a otra App hace que use las variables de la App nueva, sin aviso |
| V9 | **Tooltip y autocompletar** muestran de dónde sale el valor: "App Orders · dev", "dev", "Global de Orders", "Global" o "Sesión" |
| V10 | **Compatibilidad:** un cliente anterior trataría las variables de App como del workspace. Se sube `Clients:MinVersion` al publicar, servidor primero. Además, el `PUT` de una variable no toca la App si no viene (`null`), para que un cliente viejo no la pase a "Todas" sin querer; `Guid.Empty` la pasa a "Todas" (como `LoginId` en la App) |

## Etapa 1 — Modelo y servidor

- `Variable.AppId` (`Guid?`, null = "Todas"). Migración `VariableApps`: columna nueva y el índice único pasa de `(WorkspaceId, EnvironmentId, Key)` a `(WorkspaceId, EnvironmentId, AppId, Key)`, con los null iguales entre sí y sin las borradas, como ahora.
- Contratos: `VariableDto.AppId`, `CreateVariableRequest.AppId` y `UpdateVariableRequest.AppId` (V10). El cambio va al final de cada record con valor por defecto, para no romper llamadas existentes.
- Endpoints de `/variables`: validan que la App exista en el workspace; el `GET` acepta `appId` para filtrar. Un nombre repetido en el mismo alcance devuelve 409 con un mensaje claro, en lugar del error de la base.
- Borrar una App marca como borradas sus variables (V8), en la misma transacción.
- Sincronización: la variable depende de su App (`ParentsOf`), para que una variable creada sin conexión espere a que exista la App, y `Remap` traduce el `AppId` si la App se creó sin conexión.

## Etapa 2 — Resolución en el cliente

- `VariableContext.From(all, activeEnvironment, appId, session)` aplica V5. `appId` null deja afuera todas las variables de App: es lo que usa el login compartido (V3).
- Cada lugar que arma el contexto pasa la App que corresponde: el envío de un request o sample (su App), las pestañas del editor para el tooltip, el autocompletar y el aviso de variables sin definir (la App de la pestaña), y el login (null).
- `VariableDto.Source` o un método equivalente en el contexto dice de dónde sale cada valor vigente, para V9.
- Duplicar una App copia sus variables con ids nuevos (V8), en el mismo flujo que ya copia carpetas, requests y samples.

## Etapa 3 — Ventana de environments

- Columna **App** en la grilla de globales y de cada environment: un combo con "Todas" y las Apps del workspace, en orden. Una variable nueva nace con la App del filtro, o "Todas" si el filtro está en "Todas las Apps".
- **Filtro por App** junto al buscador (V6); el buscador sigue buscando por nombre y valor dentro de lo que deja ver el filtro.
- Validación en la grilla antes de guardar: el mismo nombre y la misma App en la misma pestaña se marcan como repetidos (V2, V7).
- Viewer: ve la columna y el filtro, sin poder editar, como el resto de la grilla.
- Textos en español e inglés.

## Cómo se prueba

- **Servidor:** crear variables con y sin App; mismo nombre en Apps distintas del mismo environment (se puede) y en la misma App (409); App de otro workspace (400); `PUT` sin `AppId` no cambia la App y con `Guid.Empty` la pasa a "Todas"; borrar la App borra sus variables; el feed de cambios trae `AppId`.
- **Sincronización:** App y variable creadas sin conexión llegan en orden y con el id traducido.
- **Resolución:** tabla de casos de V5 (cada combinación de environment/globales × App/"Todas" × sesión); el login no ve las variables de App; un request de otra App no ve las de Orders.
- **Duplicar:** la copia de la App tiene sus variables con los mismos valores y ids nuevos.
- **Interfaz:** a mano en escritorio y web, con el servidor local: columna, filtro, repetidos, tooltip con el origen, envío de dos requests de Apps distintas con `{{baseUrl}}`.

## 🧑 Checkpoint humano

Probar en la app con un workspace de varias Apps. Al publicar: servidor primero (migración `VariableApps`) y después subir `Clients:MinVersion` a la versión nueva.
