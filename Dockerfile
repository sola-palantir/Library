FROM node:24-bookworm-slim AS web
WORKDIR /src/client
COPY client/package*.json ./
RUN npm ci
COPY client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY server/Library.Api/Library.Api.csproj server/Library.Api/
COPY shared/Library.Execution/Library.Execution.csproj shared/Library.Execution/
RUN dotnet restore server/Library.Api/Library.Api.csproj
COPY server/ server/
COPY shared/ shared/
RUN dotnet publish server/Library.Api -c Release --no-restore -o /out /p:UseAppHost=false
COPY --from=web /src/client/dist /out/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out .
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 Library__DataPath=/data DOTNET_EnableDiagnostics=0
ENV Library__FfprobePath=/usr/bin/ffprobe Library__FfmpegPath=/usr/bin/ffmpeg
EXPOSE 8080
ENTRYPOINT ["dotnet", "Library.Api.dll"]
