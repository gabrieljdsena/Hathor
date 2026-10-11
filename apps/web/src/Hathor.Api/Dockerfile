# Hathor API — ASP.NET Core 10 + FFmpeg (downloads transcode to MP3 in-image).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Hathor.Domain/Hathor.Domain.csproj src/Hathor.Domain/
COPY src/Hathor.Application/Hathor.Application.csproj src/Hathor.Application/
COPY src/Hathor.Infrastructure/Hathor.Infrastructure.csproj src/Hathor.Infrastructure/
COPY src/Hathor.Api/Hathor.Api.csproj src/Hathor.Api/
RUN dotnet restore src/Hathor.Api/Hathor.Api.csproj
COPY src/ src/
RUN dotnet publish src/Hathor.Api/Hathor.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl: compose healthcheck against /api/health/ready (image ships neither
# curl nor wget). ffmpeg: downloads transcode to MP3 in-image. yt-dlp:
# fallback YouTube downloader when YoutubeExplode stream URLs are rejected.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    Database__Provider=postgres \
    Database__ConnectionString="Host=localdb;Port=5432;Database=hathor;Username=postgres" \
    Database__StorageRoot=/data/library
# NOTE: no password baked in — compose injects Database__ConnectionString
# with Password=${LOCALDB_PASSWORD} at runtime; plain `docker run` needs
# -e Database__ConnectionString=... or -e HATHOR_DB_PASSWORD=...
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Hathor.Api.dll"]
