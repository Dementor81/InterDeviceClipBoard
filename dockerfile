FROM node:24-alpine AS client
WORKDIR /src
COPY client/package.json client/package-lock.json client/tsconfig.json client/
COPY client/src client/src
RUN cd client && npm ci && npx tsc -p tsconfig.json --noEmit \
    && npx esbuild src/main.ts --bundle --format=esm --target=es2022 --outfile=../src/Clipboard.Server/wwwroot/app.js

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_VERSION=0.1.0
WORKDIR /src
COPY src/Clipboard.Server/Clipboard.Server.csproj src/Clipboard.Server/
RUN dotnet restore src/Clipboard.Server/Clipboard.Server.csproj
COPY src/Clipboard.Server src/Clipboard.Server
COPY --from=client /src/src/Clipboard.Server/wwwroot/app.js src/Clipboard.Server/wwwroot/app.js
RUN dotnet publish src/Clipboard.Server/Clipboard.Server.csproj -c Release -o /app /p:SkipTypeScript=true /p:Version=${APP_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir -p /data && chown "$APP_UID":"$APP_UID" /data
USER $APP_UID
ENV DataDir=/data \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Clipboard.Server.dll"]
