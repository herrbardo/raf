# Plan 08 — Autenticación por App

Estado: **etapas 1 y 2 implementadas** (Ticket-017). La etapa 3 está en el Ticket-025. Las variables por App son del Ticket-018.

## Objetivo

Si todos los requests de una App se autentican igual, configurarlo una sola vez y, más adelante, que RAF obtenga y renueve el token solo. En Postman esto se hacía con prescripts; acá es una función específica.

## Decisiones (acordadas con el usuario el 12/11/2026)

| Tema | Decisión |
|---|---|
| Login automático | Es parte de la **configuración de la App**, no un request más del árbol (etapa 3) |
| Dónde queda el token | En **variables de sesión**: locales de cada usuario, **no se sincronizan** |
| Renovación | Por vencimiento (`exp` del JWT, un campo de la respuesta o un tiempo fijo) y un reintento ante 401 (etapa 3) |
| Etapas | 1 y 2 primero (Ticket-017), 3 después |
| Variables por App | Fuera de este plan: Ticket-018 |

## Etapa 1 — Auth heredada de la App

- `App` tiene una `AuthSpec` (`None` por defecto), sincronizada como el resto de la App. La editan Editor y Owner; el Viewer la ve sin editar.
- `AuthType` suma **`Inherit`**: el request usa la auth de su App. Un request nuevo nace con `Inherit`; los existentes no cambian.
- Un sample que no sobreescribe la auth hereda la del request, y si esa es `Inherit`, la de la App.
- La App no puede tener `Inherit` (el servidor lo rechaza).
- Se configura en la ventana **"Autenticación de la App"**, desde el menú contextual de la App en el árbol.
- `RequestRunner.Effective` aplica la herencia: primero el sample sobre el request y después la App si quedó `Inherit`.

**Compatibilidad:** un cliente anterior no entiende `Inherit` (los enums viajan como texto) y falla al leer esos requests. Al publicar esta versión hay que subir `Clients:MinVersion` del servidor a la versión nueva del cliente, y desplegar el servidor antes de que se usen clientes nuevos.

## Etapa 2 — Variables de sesión y extracciones

**Variables de sesión**

- Son de cada usuario en su equipo: no se sincronizan, porque un token es personal y dura poco. Si se sincronizara, un compañero pisaría el token del otro y habría cambios y conflictos sin parar.
- Se separan por servidor, usuario, workspace y environment (incluido "sin environment").
- Se guardan cifradas con el almacén seguro del sistema (DPAPI/Keychain), igual que la sesión.
- Precedencia: **sesión > environment activo > globales**.
- Se tratan como secretas: la consola las enmascara y el historial no guarda su valor. El Viewer también puede usarlas: son suyas.
- Se ven y se borran en la pestaña **"Sesión"** de la barra lateral, que muestra las del environment activo.

**Extracciones** (guardar un valor de la respuesta en una variable de sesión)

- Cada request tiene una lista de extracciones, en la pestaña "Extraer": variable, origen (body o header) y ruta.
- Rutas del body: JSON path (`$.data.token`, `$['mi clave']`, `$.items[0].id`) o XPath (`/respuesta/token`, `//@id`), las mismas que copia el menú de la respuesta (Ticket-016). En un header, el nombre.
- Se aplican después de cada ejecución con respuesta **2xx**. Con otro código no se aplican, para no pisar un token bueno con una respuesta de error. Cada extracción deja una línea en la consola: aplicada o no encontrada.
- Los samples ejecutan las extracciones de su request.
- El valor de un string va sin comillas; un número o booleano, como texto; un objeto o array, como JSON compacto.
- A mano: el menú contextual del body de la respuesta suma **"Guardar en variable de sesión…"**, que pide el nombre (sugiere la clave del nodo) y guarda el valor.

## Etapa 3 — Obtener el token automáticamente (Ticket-025)

- En la configuración de la App: el request de login (método, URL, headers, body y auth propios, con variables), la ruta del token y cómo se calcula el vencimiento.
- Antes de ejecutar un request con auth `Inherit`, si no hay token o venció, se ejecuta el login, se guarda el token en una variable de sesión y se sigue.
- Ante un 401 se renueva una vez y se reintenta. Todo queda en la consola del request.
- Un Viewer no puede leer variables secretas: si las credenciales del login lo son, no va a poder obtener el token.
