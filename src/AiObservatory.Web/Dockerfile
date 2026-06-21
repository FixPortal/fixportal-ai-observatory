FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# No VITE_API_BASE — nginx proxies /api to the api container (same-origin).
# No VITE_AAD_CLIENT_ID — dev mode, no sign-in gate.
RUN npm run build

FROM nginx:stable-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
