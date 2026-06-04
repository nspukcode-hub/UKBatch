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
RUN dotnet publish src/UKBatch.Server/UKBatch.Server.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    UKBATCH_ENABLE_DASHBOARD=true
# /data = sqlite persistence mount point (ef-sqlite default: DataSource=/data/ukbatch.db).
# NOTE: an inline comment after exec-form VOLUME is mis-parsed by Docker (it treats the trailing
# text as a second mount path) — keep the comment on its own line.
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "UKBatch.Server.dll"]
