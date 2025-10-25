FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

ARG GH_OWNER
ARG GH_USERNAME
ARG GH_TOKEN
RUN dotnet nuget add source \
    --name github \
    --username ${GH_USERNAME} \
    --password ${GH_TOKEN} \
    --store-password-in-clear-text \
    "https://nuget.pkg.github.com/${GH_OWNER}/index.json"

# Copy csproj and restore as distinct layers
COPY *.csproj ./


RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out
# get rid of bloat
RUN rm -rf ./out/runtimes ./out/osu.Game.Resources.dll

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

ARG RULESET_REPO="GooGuTeam/custom-rulesets"
ENV RULESET_REPO=${RULESET_REPO}
ARG DOWNLOAD_RULESETS=false
ENV DOWNLOAD_RULESETS=${DOWNLOAD_RULESETS}

RUN apt-get update && apt-get install -y curl jq && rm -rf /var/lib/apt/lists/*

COPY --from=build-env /app/out .

RUN mkdir -p /tmp/rulesets && \
    if [ "$DOWNLOAD_RULESETS" = "true" ]; then \
        echo "Fetching latest release from GitHub repo: $RULESET_REPO"; \
        API_URL="https://api.github.com/repos/${RULESET_REPO}/releases/latest"; \
        echo "API: $API_URL"; \
        ASSETS=$(curl -sL "$API_URL" | jq -r '.assets[] | select(.name | endswith(".dll")) | .browser_download_url'); \
        if [ -z "$ASSETS" ]; then \
            echo "No DLL assets found in the latest release."; \
        else \
            echo "$ASSETS" | while read -r url; do \
                echo "Downloading $url"; \
                curl -L -o "/tmp/rulesets/$(basename "$url")" "$url"; \
            done; \
        fi; \
    else \
        echo "Skipping ruleset download."; \
    fi

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
