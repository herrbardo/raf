# Objetivo
Crear una aplicación para enviar requests a distintas APIs y trabajo en equipo. Simil postman.
Nombre: Requests and Friends
Alias: RAF

# Instrucciones
Es necesario iterar para primero diseñar distintos planes. Cada plan estará limitado por sí requieren intervención humana.

# Arquitectura minima
Hacer app desktop conectada a un backend (api + base de datos).
- Evaluar frameworks de UI (Considerar Avalonia).
- Patrones para sincronizar
- Autenticación.
- Definir stack.
- Solución ó proyectos separados?.

# Modelo base
- User: Vive en el servidor y es necesario para autenticarse
- Workspace: Contiene todos los elementos que compartirán los distintos usuarios. Es creado por uno y este podrá agregar a otros miembros.
- App: Pertenece a un workspace y contiene request
- Request: Contiene la información para ejecutar contra un endpoint.
- Sample: Es hijo de un Request y es la forma de hacer una pequeña modificación heredando el resto.

# Cliente
- Debe poder compilarse para Windows y Mac y distribuirse con instalador.
    