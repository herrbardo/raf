# Plan 07 — Embellecer la aplicación

Estado: **implementado, falta la prueba humana** (revisar en claro y oscuro). Depende de: Plan 04, para incluir sus pantallas. Es el último plan y puede ejecutarse antes o después de los Planes 05 y 06, que casi no tocan la UI.
Checkpoint humano al final: revisar la aplicación en tema claro y oscuro, y dar feedback visual.

## Objetivo

Hoy casi toda la interfaz usa el aspecto por defecto de Fluent y todo tiene el mismo peso visual. Este plan le da identidad y jerarquía sin cambiar lo que la aplicación hace.

**Fuera de alcance:** funciones nuevas, cambios de lógica o de flujos, atajos nuevos y cambios en servidor o sincronización.

Al ejecutarlo, seguir el skill [avalonia](../../.agents/skills/avalonia/SKILL.md).

## Alcance

**Base visual** (se hace primero: el resto depende de esto)

1. `src/Raf.Client/Styles/Theme.axaml`, incluido desde `App.axaml`, con los recursos de claro y oscuro: superficies, bordes, texto, acento y colores de estado.
2. Acento propio aplicado a `FluentTheme`, para que lo usen también los controles estándar.
3. Escala de tipografía, espaciado y radios definida en estilos. Inter para la UI y la monoespaciada actual de `TextField` para código y URLs.
4. Migrar a recursos los colores literales de `App.axaml`, `MainView`, `ResponseView` y `TextField`.
5. Reemplazar `MainViewModel.SyncBrush` por estado expuesto a la vista y clases de estilo. Mismo tratamiento para el punto de "cambios sin guardar".

**Identidad y jerarquía**

6. **Colores por método HTTP**, definidos como recursos, en los badges del árbol, el selector de método y el historial. El nombre del método sigue visible.
7. **Tres niveles de pestañas diferenciados:** los requests abiertos como pestañas de navegador, con cierre visible al pasar el mouse. Query/Headers/Body/Auth/Opciones y las de la respuesta, compactas y subrayadas. Quitar el `FontSize` repetido en cada `TabItem`.
8. **Superficies:** barra lateral, barra superior, área de edición y panel de respuesta con fondos o bordes que los separen.
9. **Separadores visibles:** los `GridSplitter` pasan a una línea fina, resaltada al pasar el mouse y con cursor de redimensionar.

**Componentes**

10. **Barra de la respuesta:** chip de color con el código de estado y métricas de tiempo y tamaño. Unificar los dos `TextBlock` de éxito y error con una clase condicional.
11. **Headers de la respuesta:** filas alternadas o separadas, con el nombre en color secundario.
12. **Iconos:** reemplazar `+`, `✕`, `+ App` y `Actualizar` por `PathIcon` con geometrías en un diccionario de recursos, siempre con `ToolTip`. Iconos también en el árbol, según tipo de nodo.
13. **Estados vacíos** con icono, una frase y los atajos existentes (`Ctrl+Enter`, `Ctrl+S`) en: sin pestañas, sin respuesta, historial vacío y colección vacía.
14. **Estado de envío:** la progresión y el botón Cancelar integrados a la barra de la URL o de la respuesta, sin saltos de layout.
15. **Pantallas de entrada** (Connect, Login, Cambiar contraseña): tarjeta centrada con la identidad de RAF, jerarquía clara y errores cerca del campo que los causa.
16. **Ventanas y diálogos:** Variables, Conflictos, las pantallas del Plan 04 y los diálogos de `WindowDialogs`, con los mismos márgenes, botones y superficies.

**Terminación**

17. **Barra de título integrada**, con workspace y environment dentro de ella (`ExtendClientAreaToDecorationsHint`). Es opcional: se hace solo si funciona bien en Windows y deja una degradación aceptable en macOS y Linux. Si no, se descarta y se registra el motivo acá.

## Casos borde que el plan debe resolver

| Caso | Comportamiento |
|---|---|
| El sistema cambia de tema con la app abierta (con `RAF_THEME=system`) | Todo responde al instante, incluidos `TextField` y el resaltado de sintaxis y de variables. |
| Código de estado sin respuesta (timeout, cancelado, error de red) | El chip muestra el resultado con un color neutro o de error, nunca como éxito. |
| Método personalizado o desconocido | Badge con color neutro y el texto completo o recortado. |
| Nombres y URLs muy largos | Se recortan con elipsis y sin mover el resto del layout. |
| Muchas pestañas abiertas | Siguen accesibles y el botón de cierre no tapa el nombre. |
| Usuario Viewer | Los controles deshabilitados se distinguen y siguen siendo legibles. |
| Ventana en el tamaño mínimo | Nada se superpone ni queda inaccesible. |

## Decisiones de implementación

- Sin dependencias nuevas de UI: los iconos son geometrías propias. Si alguno se toma de un set externo, debe tener licencia compatible y registrarse su atribución.
- Los viewmodels exponen estado, no brushes.
- Los textos de contraste deben ser legibles en ambos temas; el color nunca es la única señal.
- Se ejecuta en commits separados por bloque (base, jerarquía, componentes, terminación) para poder revisar y revertir cada uno.

## Cómo quedó

- **Tema principal (D7): oscuro.** La paleta se diseñó primero para oscuro y claro tiene sus propios valores, no una inversión.
- **Archivos:** `Styles/Theme.axaml` (colores de ambos temas, tipografía, radios; es el único archivo con colores literales), `Styles/Controls.axaml` (estilos por clase) y `Styles/Icons.axaml` (geometrías). `App.axaml` aplica el acento y el fondo a `FluentTheme` con `ColorPaletteResources`.
- **Iconos:** son trazos en una grilla de 16×16 que dibuja el control propio `ctl:Icon` con el color de texto heredado, en lugar de `PathIcon`, que solo rellena. Así un icono toma el color del botón o del estado que lo contiene. No hay sets externos.
- **Métodos HTTP:** el control `ctl:MethodBadge` toma el color de una pseudo-clase (`:get`, `:post`... y `:other` para métodos propios). El texto siempre se ve; los nombres largos se recortan con tooltip.
- **Estado en lugar de colores:** `MainViewModel.SyncBrush` se reemplazó por `IsSynced`, `IsOffline`, `IsInConflict` e `IsSyncPending`; `ResponseViewModel` suma `IsHttpError`, `IsNoResponse` y `ShowSending`. Sin respuesta (timeout, cancelado, error de red) el chip queda neutro, nunca verde.
- **Envío sin saltos:** Enviar y Cancelar tienen el mismo ancho y ocupan el mismo lugar. Mientras se reenvía, la respuesta anterior sigue visible con una barra de progreso fina arriba.
- **`TextField`:** fuente, colores, selección y links salen del tema; en solo lectura tiene otro fondo (Viewer y respuesta). El resaltado de JSON y XML se repinta con la paleta del tema activo al cambiarlo.
- **La app arranca en oscuro** sin importar el tema del sistema (Ticket-001). Para revisar el diseño: variable de entorno `RAF_THEME=light`, o `RAF_THEME=system` para seguir al sistema, antes de abrir la app.
- **Barra de título integrada (punto 17): descartada.** En Windows los botones del sistema quedan encima del extremo derecho de la barra superior (sincronización y usuario), y hay que reservarles un margen distinto por sistema operativo. En macOS los semáforos ocupan el extremo izquierdo y no se pudo probar. Con la barra nativa el resultado es correcto en los tres sistemas; se puede retomar cuando haya cómo probar macOS y Linux.
- **Commits:** el plan pedía un commit por bloque, pero el flujo del repositorio commitea recién con `CPS`. Queda un solo commit, con los bloques descriptos en el mensaje.
- **Verificado en local** (Windows, con capturas de antes y después): pantalla principal vacía y con request abierto, respuesta exitosa y con error, Variables, Miembros, Administración, diálogo de texto, Login (incluido el error) y Connect, en ambos temas, y la ventana en su tamaño mínimo. No se probó: el cambio de tema del sistema con la app abierta, un usuario Viewer, nombres muy largos ni muchas pestañas abiertas.

## Verificación

- `dotnet build` y `dotnet test` sin regresiones.
- Ningún color literal fuera de `Theme.axaml`, comprobado con una búsqueda en `src/Raf.Client`.
- Recorrer cada pantalla y cada caso borde en claro y en oscuro.
- Comprobar que los flujos siguen iguales: enviar, cancelar, guardar, resolver conflictos y cambiar de workspace.
- Capturas de antes y después de las pantallas principales para el checkpoint.
