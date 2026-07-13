# syntax=docker/dockerfile:1
# Embedded-mode image for the declared-parameters demo: one process hosting the REST API + Blazor
# Dashboard + local jobs, backed by in-memory storage (no Postgres/RabbitMQ). Build context is the
# REPO ROOT (compose: build.context: .) so the COPY paths below are repo-relative.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Solution-level props + central package versions + .editorconfig (carries the diagnostic severities;
# without it the in-container Release build promotes CA1848 etc. to errors under TreatWarningsAsErrors).
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./

# The sample references AspNetCore + Api + Dashboard via ProjectReference; copy the src tree + the
# sample and publish. Framework-dependent, NO trimming (Blazor Server reflection + Razor runtime).
COPY src/ src/
COPY samples/Sample.Dashboard/ samples/Sample.Dashboard/
# -f is REQUIRED: the projects declare their TFM via the plural TargetFrameworks property, and
# publish refuses to run framework-less against them.
RUN dotnet publish samples/Sample.Dashboard/Sample.Dashboard.csproj -f net10.0 -c Release -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# Run as the non-root user baked into the .NET base images (APP_UID=1654); :8080 is unprivileged.
USER $APP_UID
ENTRYPOINT ["dotnet", "Sample.Dashboard.dll"]
