# Two images, from the repository root: the app served by its mock backend,
# and the Playwright run that photographs it. Built from source every time, so
# a checkout and Docker are the only things a screenshot run needs.

# --- the client, built exactly the way production builds it ------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS client
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY app/global.json ./
COPY app/.config .config
RUN dotnet tool restore
COPY app/src/Client/package.json app/src/Client/package-lock.json src/Client/
RUN cd src/Client && npm ci
COPY app/src/Shared src/Shared
COPY app/src/Client src/Client
RUN cd src/Client && npm run build

# --- that build, in front of the mock backend --------------------------------
FROM node:22-alpine AS app
WORKDIR /srv
COPY --from=client /src/src/Client/dist ./web
COPY e2e/mock ./mock
EXPOSE 8080
HEALTHCHECK --interval=2s --timeout=2s --retries=30 \
    CMD node -e "fetch('http://localhost:8080/api/auth/me').then(r => process.exit(r.ok ? 0 : 1), () => process.exit(1))"
CMD ["node", "mock/server.js"]

# --- the screenshot run ------------------------------------------------------
# The base image is pinned to the same Playwright version as package.json; the
# browsers are baked into it, so nothing is downloaded at run time.
FROM mcr.microsoft.com/playwright:v1.63.0-noble AS tests
WORKDIR /e2e
COPY e2e/package.json e2e/package-lock.json ./
RUN npm ci
COPY e2e/playwright.config.js ./
COPY e2e/mock ./mock
COPY e2e/tests ./tests
CMD ["npx", "playwright", "test"]
