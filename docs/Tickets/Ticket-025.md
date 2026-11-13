# Tipo
FEATURE

# Descripción
Etapa 3 del Plan 08 (docs/plans/08-auth-por-app.md): que la App obtenga y renueve el token sola.

- En la configuración de la App: el request de login (método, URL, headers, body y auth propios, con variables), la ruta del token y cómo se calcula el vencimiento (exp del JWT, un campo de la respuesta o un tiempo fijo).
- Antes de ejecutar un request con auth Inherit, si no hay token o venció, se ejecuta el login, se guarda el token en una variable de sesión y se sigue.
- Ante un 401 se renueva una vez y se reintenta. Todo queda en la consola del request.
- Un Viewer no puede leer variables secretas: si las credenciales del login lo son, no va a poder obtener el token.

Parte de lo hecho en el Ticket-017 (etapas 1 y 2: auth heredada de la App y variables de sesión).

# Estado
CREADO
