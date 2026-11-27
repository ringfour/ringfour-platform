# Opt-in source preview target; Compose serves the compiled production target.
FROM node:24-alpine AS development
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

FROM development AS build
ARG VITE_AUTH0_DOMAIN
ARG VITE_AUTH0_CLIENT_ID
ARG VITE_AUTH0_AUDIENCE=https://api.ringfour.eu
ENV VITE_AUTH0_DOMAIN=$VITE_AUTH0_DOMAIN \
    VITE_AUTH0_CLIENT_ID=$VITE_AUTH0_CLIENT_ID \
    VITE_AUTH0_AUDIENCE=$VITE_AUTH0_AUDIENCE
RUN test -n "$VITE_AUTH0_DOMAIN" && test -n "$VITE_AUTH0_CLIENT_ID" && npm run build

FROM nginx:1.28-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236 AS production
ENV API_PROXY_TARGET=http://api:8080
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
