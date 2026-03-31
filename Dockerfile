FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Aura.sln ./
COPY src/Aura.Core/Aura.Core.csproj src/Aura.Core/
COPY src/Aura.Infrastructure/Aura.Infrastructure.csproj src/Aura.Infrastructure/
COPY src/Aura.Api/Aura.Api.csproj src/Aura.Api/
COPY src/Aura.Worker/Aura.Worker.csproj src/Aura.Worker/
COPY tests/Aura.Tests/Aura.Tests.csproj tests/Aura.Tests/
RUN dotnet restore

COPY . .

# --- API target ---
FROM build AS publish-api
RUN dotnet publish src/Aura.Api/Aura.Api.csproj -c Release -o /app/api

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
RUN groupadd -r aura && useradd -r -g aura -s /sbin/nologin aura
WORKDIR /app
COPY --from=publish-api /app/api .
RUN chown -R aura:aura /app
USER aura
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD curl -sf http://localhost:8000/health || exit 1
ENTRYPOINT ["dotnet", "Aura.Api.dll"]

# --- Worker target ---
FROM build AS publish-worker
RUN dotnet publish src/Aura.Worker/Aura.Worker.csproj -c Release -o /app/worker

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
ARG DOCKER_GID=988
RUN groupadd -r aura && useradd -r -g aura -s /sbin/nologin aura \
    && groupadd -g ${DOCKER_GID} docker && usermod -aG docker aura

# Install PowerShell Core
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget apt-transport-https \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/* packages-microsoft-prod.deb

# Install Az PowerShell modules for multi-executor support
RUN pwsh -Command "Set-PSRepository PSGallery -InstallationPolicy Trusted" \
    && pwsh -Command "Install-Module -Name Az.Accounts -Scope AllUsers -Force" \
    && pwsh -Command "Install-Module -Name Az.Compute -Scope AllUsers -Force" \
    && pwsh -Command "Install-Module -Name Az.Network -Scope AllUsers -Force"

WORKDIR /app
COPY --from=publish-worker /app/worker .

# Copy multi-executor scripts and templates
COPY Essences/Aura/multi-executor-vm/scripts/ /app/scripts/
COPY Essences/Aura/multi-executor-vm/templates/ /app/templates/

RUN chown -R aura:aura /app
USER aura
ENTRYPOINT ["dotnet", "Aura.Worker.dll"]
