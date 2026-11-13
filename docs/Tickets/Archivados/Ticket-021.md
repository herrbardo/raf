# Tipo
BUG

# Descripción
Si apreto tab en el nombre de un header me tendría que llevar al textbox de su valor. Revisar que el tab permita navegar por los controles a lo largo de la app.

Causa probable (diagnóstico del Ticket-002): los campos usan ctl:TextField, basado en AvaloniaEdit, que toma Tab para indentar. TextField ya le quita el Enter a los campos de una línea (OnPreviewKeyDown) pero no el Tab. En los editores multilínea de body, Tab debería seguir indentando; en los de una línea y en los de solo lectura, pasar al control siguiente (Shift+Tab al anterior).

Surgió de la prueba humana del Ticket-002.

# Estado
CREADO
