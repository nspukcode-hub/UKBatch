# syntax=docker/dockerfile:1
# Multi-stage build for the Sample.WorkerMode.Notification worker-mode image.
# Build context is the REPO ROOT (compose: build.context: .) so the COPY paths below are repo-relative.
# Narrower reference closure than the server image: Abstractions + Core + AspNetCore + Worker +
# Transport.RabbitMQ + this sample. NO Api / Dashboard / Storage — a RabbitMQ worker receives dispatch
# over the broker, not HTTP endpoints.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Copy solution-level props + central package versions FIRST so `dotnet restore` is cached across
#    source edits. (No root nuget.config exists — do NOT COPY one; it would fail the build.)
# .editorconfig carries the diagnostic severities (CA1848=suggestion, CA2007=none, CA1031=none, …).
# Without it the in-container Release build promotes those to errors under TreatWarningsAsErrors.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./

# 2) Copy ONLY the csproj files in this worker's reference closure (paths preserved) then restore.
COPY src/UKBatch.Abstractions/UKBatch.Abstractions.csproj                            src/UKBatch.Abstractions/
COPY src/UKBatch.Core/UKBatch.Core.csproj                                            src/UKBatch.Core/
COPY src/UKBatch.AspNetCore/UKBatch.AspNetCore.csproj                                src/UKBatch.AspNetCore/
COPY src/UKBatch.Worker/UKBatch.Worker.csproj                                        src/UKBatch.Worker/
COPY src/UKBatch.Transport.RabbitMQ/UKBatch.Transport.RabbitMQ.csproj                src/UKBatch.Transport.RabbitMQ/
COPY samples/Sample.WorkerMode.Notification/Sample.WorkerMode.Notification.csproj    samples/Sample.WorkerMode.Notification/
RUN dotnet restore samples/Sample.WorkerMode.Notification/Sample.WorkerMode.Notification.csproj

# 3) Copy the source needed to build (src + this sample) + publish. Framework-dependent, NO trimming.
COPY src/ src/
COPY samples/Sample.WorkerMode.Notification/ samples/Sample.WorkerMode.Notification/
RUN dotnet publish samples/Sample.WorkerMode.Notification/Sample.WorkerMode.Notification.csproj \
    -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "Sample.WorkerMode.Notification.dll"]
