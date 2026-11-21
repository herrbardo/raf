# Tipo
FEATURE

# Descripción
Publicar la imagen del servidor (src/Raf.Server/Dockerfile) en Docker Hub. Hay que definir:
- si se publica a mano con un script o con GitHub Actions en cada release;
- los tags (versión y latest);
- las arquitecturas (amd64 y arm64);
- una guía para levantarla con Postgres.

El usuario de Docker Hub y el token van como secretos del repo o de la máquina, nunca versionados.

# Estado
CREADO
