FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# ffmpeg libgdiplus required for ffmpeg
RUN apt-get update && apt-get install ffmpeg libgdiplus -y


FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-aot AS build
WORKDIR /src
COPY .git .git
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["Directory.Packages.props", "Directory.Packages.props"]
COPY ["gen/Pitwall.Data.SourceGeneration/Pitwall.Data.SourceGeneration.csproj", "gen/Pitwall.Data.SourceGeneration/"]
COPY ["gen/Pitwall.Console.SourceGeneration/Pitwall.Console.SourceGeneration.csproj", "gen/Pitwall.Console.SourceGeneration/"]
COPY ["Pitwall.Data/Pitwall.Data.csproj", "Pitwall.Data/"]
COPY ["Pitwall.Console/Pitwall.Console.csproj", "Pitwall.Console/"]
RUN dotnet restore "Pitwall.Console/Pitwall.Console.csproj"
COPY . .
WORKDIR "/src/Pitwall.Console"
ARG TARGETARCH
RUN dotnet publish "Pitwall.Console.csproj" -o /app/publish -a "$TARGETARCH" /p:PublicRelease=true /p:PublishTrimmed=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV PITWALL_DATADIRECTORY=/data
ENV PITWALL_LOGDIRECTORY=/logs

ENTRYPOINT ["./pitwall"]
