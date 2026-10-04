# hathor-web — Vite build served by nginx, /api + /hubs proxied to the API.
FROM node:22-alpine AS build
WORKDIR /web
COPY frontend/hathor-web/package.json frontend/hathor-web/package-lock.json* ./
RUN npm install --no-audit --no-fund
COPY frontend/hathor-web/ ./
RUN npm run build

FROM nginx:alpine AS final
COPY frontend/hathor-web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 80
