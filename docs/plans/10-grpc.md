# Plan 10 — gRPC

Estado: **aprobado** por el usuario el 24/11/2026, con las decisiones G1 a G4. **Implementado** (las cuatro etapas, Ticket-035), sin probar todavía en la app de escritorio contra un servidor real. La 3 se hizo antes que la 2 para reusar la conversación del WebSocket (Ticket-036). Sale del Ticket-035 (feedback del 24/11/2026).

## Objetivo

Que un request de RAF pueda ser una llamada gRPC, con la misma experiencia que un request HTTP: se guarda en el árbol, se sincroniza, usa variables, auth y login de la App, y muestra la respuesta, la consola y el historial.

## Decisiones (acordadas con el usuario el 24/11/2026)

| Tema | Decisión |
|---|---|
| Cómo se conocen los servicios | **Server reflection y archivos .proto**: reflection si el servidor lo tiene, .proto cargados en el workspace si no |
| Tipos de llamada | **Todos**: unaria, server streaming, client streaming y bidireccional |
| Cliente web | **Solo desktop por ahora**: en web el request gRPC se ve y se edita, pero no se envía, y la app explica por qué |

Aprobadas con el plan, el mismo día:

| # | Decisión |
|---|---|
| G1 | Los samples de un request gRPC sobreescriben el mensaje y la metadata, como un sample HTTP sobreescribe body y headers |
| G2 | Un **conjunto de .proto** por workspace (como los logins compartidos), que cada request gRPC elige; no por App, porque varias Apps suelen compartir contratos |
| G3 | Las extracciones leen el **último mensaje** recibido (JSON path) y la metadata de respuesta y los trailers (por nombre) |
| G4 | Sin tope de duración en streaming; el timeout del request es el **deadline** gRPC solo en las unarias y en server streaming |

## Cómo quedó la etapa 1

- **Modelo:** `GrpcSpec` (servicio, método, origen y conjunto de .proto) en `RequestSpec.Grpc`; `Method` vale `GRPC`, que el árbol muestra como un badge "gRPC". El servidor valida que `Method` y `Grpc` coincidan y que el servicio y el método no excedan 500 y 200 caracteres.
- **Proyecto `Raf.Client.Grpc`:** lo referencia solo la app de escritorio. `ProtoJson` traduce JSON ↔ protobuf y arma la plantilla, `DescriptorSet` junta los descriptores (completa los .proto de Google que falten), `ReflectionClient` habla reflection v1 o v1alpha y `GrpcEngine` hace la llamada con un canal por dirección.
- **`Raf.Client.Core`:** `IGrpcEngine` y la rama gRPC de `RequestRunner`, que arma la metadata (headers y auth) y deja el resultado en el mismo `RunResult` que HTTP: el mensaje como body JSON, la metadata y los trailers como headers y el estado en `GrpcStatus`. Así la respuesta, las extracciones (con estado OK en lugar de 2xx), la consola y el historial funcionan sin cambios.
- **Dirección:** `https://` o `grpcs://` con TLS, `http://` o `grpc://` sin TLS; sin esquema se pide uno, para no adivinar si hay TLS.
- **UI:** "Nuevo request gRPC" en el menú de Apps y carpetas, y una pestaña propia con la dirección, el selector de métodos (se piden al abrir y con el botón de recargar), el mensaje con "Usar plantilla" (que se aplica sola si el mensaje está vacío), Metadata, Auth, Extraer y Opciones.

## Cómo quedó la etapa 3

- **Motor:** `IGrpcEngine.StartStreamAsync` abre server streaming, client streaming o bidireccional y devuelve un `IGrpcStream`: los mensajes llegan por `Received` (en JSON, o con el error si no se pudieron leer) y el final por `Completed`, con el estado, la metadata y los trailers. `SendAsync` manda un mensaje, `CompleteSendingAsync` termina el envío y `Cancel` corta (termina en CANCELLED). Según G4, el timeout es deadline solo en server streaming.
- **Modelo:** `GrpcSpec.Messages` guarda los mensajes preparados de client streaming y bidireccional; el tipo `PreparedMessage` es el mismo del WebSocket (antes `WebSocketMessage`, con el mismo JSON). El servidor los valida igual: hasta 200, de hasta 1 MB.
- **UI:** con un método de streaming, la respuesta se reemplaza por la conversación del WebSocket (`ConversationView`). En server streaming se manda el mensaje de la pestaña al abrir. En client streaming y bidireccional la pestaña "Mensajes" tiene los mensajes preparados (`PreparedMessagesView`, compartida con el WebSocket; uno nuevo nace con la plantilla del método) y la conversación suma el cuadro para escribir y "Terminar de enviar". "Enviar" y Ctrl+Enter abren la llamada o, abierta, mandan lo escrito; "Cancelar" la corta. El login automático corre antes de abrir.
- **Al terminar:** el estado con su detalle, la metadata, los trailers y un resumen (enviados, recibidos y duración); las extracciones (G3) con el último mensaje recibido y la metadata y los trailers, si terminó en OK; y una línea en el historial.

## Cómo quedó la etapa 4

- **Samples (G1):** los requests gRPC vuelven a tener "Nuevo sample". La pestaña del sample es la misma de HTTP adaptada: muestra el servicio y el método del request (no se cambian), sin método HTTP ni query, con "Metadata" y "Mensaje" para sobreescribir, y también la dirección y la auth. Usa el conjunto de .proto del request. Un sample se envía como llamada unaria: el de un método con streaming avisa que se abre desde el request.
- **Reintento:** el login automático se renueva una vez y se reintenta ante `UNAUTHENTICATED` (código 16), igual que ante un 401. Vale para las unarias y los samples; una llamada con streaming ya abierta no se reintenta.
- **Web:** el aviso de que gRPC solo se envía desde escritorio está desde la etapa 1.
- **Documentación:** README (descripción y proyecto `Raf.Client.Grpc`) y este plan.

## Al publicar

Hay migración (`ProtoSets`) y tipos nuevos en los datos (`grpc` y `webSocket` en el spec, el tipo de cambio `ProtoSet`): un cliente anterior no los entiende y, si guarda un request gRPC o WebSocket, borra esa parte. Hay que desplegar primero el servidor y después subir `Clients:MinVersion` a la versión nueva del cliente, como en la v0.3.0.

## Cómo quedó la etapa 2

- **Servidor:** entidad `ProtoSet` (nombre y archivos con ruta y contenido, en jsonb), migración `ProtoSets`, endpoints `/workspaces/{ws}/proto-sets` (editan Editor y Owner) y tipo de cambio `ProtoSet` en la sincronización. Topes (`ProtoFile.Problem`): hasta 200 archivos y 4 MB por conjunto, rutas relativas con "/" y sin "..", sin repetidas. Un request gRPC con origen `ProtoSet` tiene que apuntar a un conjunto que exista, y un conjunto en uso no se borra (el error nombra los requests).
- **Sincronización:** el request depende de su conjunto, así que sin conexión se pueden crear los dos y al volver el conjunto se envía primero; si un conflicto le cambia el id al conjunto, el request lo sigue. La caché local aplica las mismas reglas que el servidor.
- **Compilación:** `ProtoCompiler` usa `protobuf-net.Reflection` (con alias `pbnet`, porque define tipos con los mismos nombres que Google.Protobuf) más `protobuf-net` para serializar los descriptores, que llegan al mismo `DescriptorSet` del reflection. Los imports se resuelven dentro del conjunto; los de `google/protobuf` vienen con la librería. Los errores salen con archivo, línea y columna. El motor compila una vez por conjunto y versión.
- **UI:** botón ".proto" en la barra superior, con la ventana "Archivos .proto": conjuntos (nuevo, renombrar, eliminar), sus archivos ("Agregar archivos…", "Agregar carpeta…" que conserva las rutas relativas para los imports, quitar), la ruta y el contenido editables y "Compilar" para ver los errores o la cantidad de métodos antes de guardar (en web la compilación no está). En la pestaña gRPC, "Servicios" elige entre el server reflection y un conjunto; con un conjunto los métodos se conocen sin conectarse.

## Cómo funciona

**Modelo.** `RequestSpec` suma `Grpc` (null en un request HTTP) con: servicio y método completos (`paquete.Servicio/Metodo`), el origen de los descriptores (reflection o el id de un conjunto de .proto), el mensaje en JSON (en client streaming y bidireccional, la lista de mensajes preparados) y las opciones de TLS. La dirección va en `Url` (`https://host:443` con TLS, `http://host:50051` sin TLS) y la metadata en `Headers`, así que variables, auth heredada y login de la App funcionan igual: un Bearer viaja como metadata `authorization`. El spec ya se guarda como JSON en la base, así que no hace falta migración para los requests.

**Descriptores.** Con reflection, el cliente le pide al servidor gRPC los descriptores (`grpc.reflection.v1`, con `v1alpha` como alternativa) y los guarda en memoria por dirección. Con .proto, el cliente los compila con el parser administrado de `protobuf-net.Reflection` (sin `protoc` ni código nativo) y lleva el resultado al mismo formato de descriptores que reflection, para que el resto no distinga el origen.

**Mensajes dinámicos (riesgo principal).** `Google.Protobuf` para .NET no tiene mensajes dinámicos: no puede armar ni leer un mensaje sin el código generado. RAF tiene que traducir por su cuenta entre JSON y el formato binario de protobuf a partir del descriptor: escalares, enums, `repeated`, `map`, `oneof`, mensajes anidados y la representación JSON de los tipos conocidos (`Timestamp`, `Duration`, wrappers, `Struct`/`Value`, `Empty`; `Any` solo si su tipo está entre los descriptores). Es la pieza más grande y la que más tests necesita; va en `Raf.Client.Core`, sin UI.

**Llamada.** `Grpc.Net.Client` con un marshaller de bytes: un `Method<byte[], byte[]>` armado en tiempo de ejecución sirve para cualquier servicio y para los cuatro tipos de llamada. "Aceptar certificados inválidos" y el timeout funcionan como en HTTP.

**Web.** El navegador no puede hablar gRPC (HTTP/2 con trailers) y el relay actual es HTTP/1. En web, enviar un request gRPC muestra "gRPC solo se puede enviar desde la app de escritorio". Si más adelante se quiere, la alternativa es un relay gRPC en el servidor (ticket aparte).

## Etapas

### Etapa 1 — Unaria con reflection

- `GrpcSpec` en `Raf.Domain`, con validación en el servidor (servicio y método no vacíos, dirección http/https).
- En `Raf.Client.Core`: cliente de reflection, caché de descriptores por dirección, traductor JSON ↔ protobuf y ejecución unaria en `RequestRunner`, con consola (conexión, metadata enviada, estado gRPC, trailers) e historial.
- Árbol: "Nuevo request gRPC" en el menú de Apps y carpetas; el nodo muestra un badge `gRPC`.
- Editor: dirección, selector de servicio y método (se llena con reflection, con botón para recargar), mensaje en JSON con una plantilla armada del descriptor, metadata, auth y opciones.
- Respuesta: estado gRPC (código y mensaje), el mensaje en JSON, metadata de respuesta y trailers.
- Tests: traductor (todos los tipos, ida y vuelta), plantilla, armado de la llamada; y un servidor gRPC de prueba con reflection dentro de los tests.

### Etapa 2 — Conjuntos de .proto

- Servidor: entidad nueva `ProtoSet` (nombre y archivos con su ruta y contenido), endpoints, permisos (editan Editor y Owner) y tipo de cambio nuevo en la sincronización. Necesita **migración** y un tope de tamaño por conjunto.
- Cliente: sincronización del tipo nuevo, ventana "Archivos .proto" (como la de logins compartidos) para cargar, reemplazar y borrar archivos, y mostrar los errores de compilación con archivo y línea.
- El request elige reflection o un conjunto; los `import` se resuelven dentro del conjunto, y los de Google (`google/protobuf/*.proto`) vienen incluidos.

### Etapa 3 — Streaming

- Server streaming: la respuesta pasa a ser una lista de mensajes con hora y número, que se llena mientras llegan, con botón para cancelar.
- Client streaming y bidireccional: el editor tiene una lista de mensajes preparados; al enviar se abre la conversación, donde se manda cada mensaje (los preparados o uno escrito en el momento), se cierra el envío ("terminar de enviar") o se cancela la llamada. Los mensajes enviados y recibidos se ven intercalados en orden.
- La consola y el historial guardan la cantidad de mensajes y la duración; el historial no guarda el contenido de cada mensaje.

### Etapa 4 — Integración y cierre

- Samples (G1), extracciones (G3), login automático y reintento ante `UNAUTHENTICATED` (el equivalente del 401).
- Aviso en el cliente web.
- Documentación (README y este plan) y textos en español e inglés.

## Compatibilidad

Un cliente anterior ignora el campo `Grpc` del spec: vería el request como un GET a la dirección y, si lo guardara, **borraría la parte gRPC**. Además no conoce el tipo de cambio `ProtoSet` de la etapa 2. Al publicar hay que desplegar primero el servidor y después subir `Clients:MinVersion` a la versión nueva del cliente, como en la v0.3.0.

## Dependencias nuevas

`Grpc.Net.Client`, `Grpc.Reflection` (cliente de reflection; también sirve para el servidor de prueba de los tests), `Google.Protobuf` y `protobuf-net.Reflection`. Todas con licencia permisiva (Apache 2.0 o BSD); solo las usa el cliente de escritorio y `Raf.Client.Core`.

## Riesgos

- **Traductor JSON ↔ protobuf:** es código propio que tiene que seguir la especificación del mapeo JSON de proto3. Se mitiga con tests de ida y vuelta contra mensajes generados por `Google.Protobuf`.
- **Parser de .proto:** `protobuf-net.Reflection` cubre proto2 y proto3, pero puede diferir de `protoc` en casos raros (opciones personalizadas, ediciones nuevas). Si un archivo no compila, el error lo dice y queda reflection.
- **Tamaño del cliente web:** las librerías nuevas no se tienen que incluir en el build de WebAssembly; la ejecución gRPC va detrás de `ClientHost`.

## 🧑 Checkpoint humano

Aprobar el plan y las decisiones pendientes G1 a G4. Después de cada etapa, probar en la app contra un servidor gRPC real del equipo, si hay uno con reflection.
