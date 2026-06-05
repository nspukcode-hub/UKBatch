# syntax=docker/dockerfile:1
# Multi-stage build for the UKBatch.Server orchestrator + dashboard image.
# Build context is the REPO ROOT (compose: build.context: .) so the COPY paths below are repo-relative.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Copy solution-level props + central package versions FIRST so `dotnet restore` is cached across
#    source edits (only re-runs when a csproj or these files change).
# .editorconfig carries the diagnostic severities (CA1848=suggestion, CA2007=none, CA1031=none, …).
# Without it the in-container Release build promotes those to errors under TreatWarningsAsErrors.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./

# 2) Copy ONLY the csproj files in the server's reference closure (paths preserved) then restore.
COPY src/UKBatch.Abstractions/UKBatch.Abstractions.csproj                                 src/UKBatch.Abstractions/
COPY src/UKBatch.Core/UKBatch.Core.csproj                                                 src/UKBatch.Core/
COPY src/UKBatch.AspNetCore/UKBatch.AspNetCore.csproj                                     src/UKBatch.AspNetCore/
COPY src/UKBatch.Api/UKBatch.Api.csproj                                                   src/UKBatch.Api/
COPY src/UKBatch.Dashboard/UKBatch.Dashboard.csproj                                       src/UKBatch.Dashboard/
COPY src/UKBatch.Storage.EntityFrameworkCore/UKBatch.Storage.EntityFrameworkCore.csproj   src/UKBatch.Storage.EntityFrameworkCore/
COPY src/UKBatch.Transport.Http/UKBatch.Transport.Http.csproj                             src/UKBatch.Transport.Http/
COPY src/UKBatch.Transport.RabbitMQ/UKBatch.Transport.RabbitMQ.csproj                     src/UKBatch.Transport.RabbitMQ/
COPY src/UKBatch.Server/UKBatch.Server.csproj                                             src/UKBatch.Server/
RUN dotnet restore src/UKBatch.Server/UKBatch.Server.csproj

# 3) Copy the rest of the source + publish. Framework-dependent, NO trimming/AOT — PublishTrimmed
#    breaks Blazor Server reflection + Razor runtime compilation (decision #7).
COPY src/ src/
# -f is REQUIRED: the project declares its TFM via the plural TargetFrameworks property
# (multi-target repo convention), and publish refuses to run framework-less against it.
RUN dotnet publish src/UKBatch.Server/UKBatch.Server.csproj -f net10.0 -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
# Auth posture is FAIL-CLOSED: the server refuses to start unless the operator explicitly sets
# UKBATCH_ALLOW_ANONYMOUS=true (only behind a trusted network / external auth gateway) or
# UKBATCH_DEV_AUTH=true (header-trusting demo scheme). Deliberately NOT defaulted here.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    UKBATCH_ENABLE_DASHBOARD=true
# /data = sqlite persistence mount point (ef-sqlite default: DataSource=/data/ukbatch.db).
# Created + chowned BEFORE the VOLUME declaration so a fresh named volume inherits the non-root
# ownership on first use (content after a VOLUME line is discarded). Pre-existing volumes created
# by older root-running images need a one-time `chown -R 1654 /data` on the host/volume.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
# NOTE: an inline comment after exec-form VOLUME is mis-parsed by Docker (it treats the trailing
# text as a second mount path) — keep the comment on its own line.
VOLUME ["/data"]
# Run as the non-root user baked into the .NET base images (APP_UID=1654). The app binds :8080
# (unprivileged), so root grants nothing but risk — any RCE in the web surface would otherwise
# run with full container privileges.
USER $APP_UID
ENTRYPOINT ["dotnet", "UKBatch.Server.dll"]
