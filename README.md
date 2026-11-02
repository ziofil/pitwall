<!-- omit in toc -->
# Pitwall

Pitwall is an open source F1 Live Timing client.

> [!NOTE]
> Pitwall is a fork of [undercut-f1](https://github.com/JustAman62/undercut-f1) by Aman Dhoot, modified by Filippo Miatto.
> It is distributed under the same [GPL-3.0 license](LICENSE). See [Notice](#notice) for details.

`pitwall` is a TUI application that shows a team's pit wall during F1 sessions: pick a team and follow its two cars on pages modelled on what engineers and strategists look at. It uses `Pitwall.Data` for the live timing feed, and records the data for future session replays.
F1 live broadcasts are usually delayed by some indeterminable amount (usually 30-60 seconds), so the TUI allows you to delay the data being displayed so that you can match up what you see on your screen to what you see on your TV.

The `Pitwall.Data` library is provided to facilitate connectivity with the F1 Live Timing data stream, and handle all the processing of the incoming data. It also allows for "simulated" streams, where previously recorded data streams can be played back to allow for easy development/testing.

Feature Highlights:

- An official-style [timing monitor](#the-pit-wall) with the team's cars marked
- A [strategy page](#the-pit-wall) with a race trace, pace and tyre degradation, gap trends, a pit-now rejoin prediction and undercut range
- Waveform [telemetry](#the-pit-wall) traces for both cars, current lap against the previous one
- A lap ring and circuit map where cars glide smoothly between feed updates
- Race control messages and the team's radio, with playback and transcription
- [Pause the session clock](#external-session-clock-sync) when you pause external video players (such as Kodi), so that your timing screen remains in sync with your feed.

> [!WARNING]
> Since the 2025 Dutch GP, the F1 Live Timing feed no longer freely publishes some of the data we need to provide all existing functionality.
>
> The following functionality is now only available to those who have an active F1 TV subscription:
>
> - Car positions on the track page
> - Car telemetry (speed, throttle, brake, gear, RPM, DRS)
> - Pit Stop times (in box and in pitlane)
>
> Importing a session after it finishes still fetches all the data, so all functionality is available if you use pitwall after a race finishes.
>
> Linking your F1 TV account with pitwall is as simple as running `pitwall login` before starting your session, and following the instructions displayed. See [F1 TV Account Login](#f1-tv-account-login) for details.

<!-- omit in toc -->
## Table of Contents

- [Inspiration](#inspiration)
- [The Pit Wall](#the-pit-wall)
- [Getting Started with `pitwall`](#getting-started-with-pitwall)
  - [Installation](#installation)
    - [Prerequisites](#prerequisites)
    - [Install and run as a dotnet tool](#install-and-run-as-a-dotnet-tool)
    - [Install and run the standalone executable](#install-and-run-the-standalone-executable)
    - [Run using the docker image](#run-using-the-docker-image)
      - [Known Issues with Docker](#known-issues-with-docker)
    - [Run directly from Source](#run-directly-from-source)
  - [Start Timing for a Live Session](#start-timing-for-a-live-session)
  - [Start Timing for a Pre-recorded Session](#start-timing-for-a-pre-recorded-session)
  - [Download a previous session data for replay](#download-a-previous-session-data-for-replay)
  - [During the Session](#during-the-session)
    - [Managing Delay](#managing-delay)
    - [Using the Cursor](#using-the-cursor)
- [Configuration](#configuration)
  - [Default Directories](#default-directories)
  - [F1 TV Account Login](#f1-tv-account-login)
- [Logging](#logging)
- [Alternate Key Binds](#alternate-key-binds)
- [Data Recording and Replay](#data-recording-and-replay)
- [API](#api)
  - [Control API](#control-api)
  - [Data API](#data-api)
- [External Session Clock Sync](#external-session-clock-sync)
- [Notice](#notice)

## Inspiration

This project is heavily inspired by the [FastF1 project by theOehrly](https://github.com/theOehrly/Fast-F1). They did a lot of the work understanding the SignalR stream coming from the F1 Live Timing service. Visit their project if you'd like to do any sort of data analysis on past F1 events, or gather live timing data using their module.

## The Pit Wall

Start a session, then pick a team. Five pages follow the layout and colour conventions of what teams look at on the pit wall, built from the public live timing feed. Everything centres on the selected team's two cars, which are marked in the team colour.

| Key | Page | What it shows |
| --- | --- | --- |
| `1` | Timing | A copy of the official timing monitor: cyan positions and headers, purple/green/yellow sector times, session-best sectors above the sector columns, and the clocks in yellow along the bottom. |
| `2` | Strategy | A race trace (each car's time against a reference pace, so pace, stops and gaps read at a glance), and per car: tyre and stint, pace and degradation, the gaps ahead and behind with their trend, where it would rejoin if it pitted now, and rivals in undercut range. |
| `3` | Telemetry | Waveform traces against lap distance (speed, throttle, brake, gear, RPM, DRS), with the current lap over the previous one and a legend of live values. |
| `4` | Track | A lap ring showing where every car is around the lap, with the latest race control messages inside, next to the circuit map. |
| `5` | Comms | Race control messages coloured by type, with those about the team marked, alongside the team's radio. `Enter` plays and `T` transcribes. |

Use `◄`/`►` or `1`–`5` to move between pages, `Tab` to switch to the next team, and `G` to pick a team from the list. Press `W` on the main screen to get back to the pit wall.

The data comes from the public feed only, so the pages show what can be derived from it. Teams' own car telemetry (tyre temperatures, energy, fuel and so on) isn't available. The pit loss is estimated from stops already made in the session, comparing in- and out-laps with the cars that didn't stop on the same laps. Telemetry and car positions need an [F1 TV account](#f1-tv-account-login) during live sessions.

## Getting Started with `pitwall`

### Installation

> [!IMPORTANT]
> Pitwall has not been published to NuGet or Docker Hub yet, so the `dotnet tool` and Docker instructions below won't work until it is.
> For now, [run it directly from source](#run-directly-from-source) or download an executable from a GitHub release.

#### Prerequisites

Pitwall tries to statically link as many dependencies as possible to make installation and usage easy.
There are however some utilities that may need to be installed for some functionality:

- Team Radio audio playback uses platform-specific command-line executables to play audio files.
  - On Linux, you need `mpg123` available on the `PATH`. For apt-based systems, you can install with `apt install mpg123`
  - On Mac, you need `afplay` available on the `PATH`. This should be installed by default.
  - On Windows, we only support audio playback via FFmpeg (`ffplay`) - see below for installation instructions.
  - On Linux/Mac, you can use the [`preferFfmpegPlayback` configuration](#configuration) to use `ffplay` instead of `mpg123`/`afplay`
- Team Radio transcription relies on FFmpeg and Whisper. Whisper models are downloaded on demand (after user confirmation) in the app. See the [FFmpeg download page](See <https://www.ffmpeg.org/download.html>) for details on how to install.
  - On Linux apt-based systems, you can install with `apt install ffmpeg`
  - On Mac with brew, you can install with `brew install ffmpeg`
  - On Windows with WinGet, you can install with `winget install ffmpeg`
- Terminal graphics rely on [SkiaSharp](https://github.com/mono/SkiaSharp). I've statically linked all the skia libs, so you shouldn't need to download skia. However, skia does rely on `libfontconfig` which may not be installed on your system by default.
  - On Linux apt-based systems, you can install with `apt install libfontconfig`
  - On Mac with brew, you can install with `brew install fontconfig`
- Guided F1 TV sign in (see [F1 TV Account Login](#f1-tv-account-login)) uses the <https://github.com/webview/webview> library to launch a WebView and guide you through sign in. This isn't required functionality, F1 TV sign-in can be done manually or completely ignored.
  - On Windows/Mac, this should work with the natively installed WebView2 and WebKit respectively.
  - On Linux, this uses WebKitGTK which may or may not already be installed. See [webview's documentation for more](https://github.com/webview/webview#linux-and-bsd).

#### Install and run as a dotnet tool

`pitwall` is available as a `dotnet` tool from NuGet, which means it can be installed system-wide simply by running:

```sh
# Install globally using the -g flag
dotnet tool install -g pitwall

# Assuming the dotnet tools directory is on your path, simply execute pitwall
pitwall
```

This method is recommended as it is easy to keep the app updated using `dotnet tool update -g pitwall`. You'll need the .NET 9 SDK installed to use this installation method. If you'd rather not install the SDK, try the [standalone installation option below](#install-and-run-the-standalone-executable).

#### Install and run the standalone executable

Standalone executables are attached to each GitHub release. Download the executable for your system OS/architecture and simply run it directly. The list of artifacts are available on the [release page for the latest release](https://github.com/ziofil/pitwall/releases/latest). These executables are static builds so don't require the `dotnet` runtime to be present.

```sh
# Download the latest executable (in this case for osx-arm64)
curl https://github.com/ziofil/pitwall/releases/latest/download/pitwall-osx-arm64 -o ./pitwall -L

# Execute pitwall to start the TUI
./pitwall
```

#### Run using the docker image

Docker images are pushed to Dockerhub containing the executable.
The image expects a volume to be mounted at `/data` to store/read session recordings.
If this is not provided, the application will only work for live sessions and you'll lose recorded data.

If provided, the directory you are mapping must already exist, as the docker image will not have the required permissions to create it for you.

If you are using Wezterm or iTerm as your terminal, you'll need to pass through the TERM_PROGRAM environment variable
to make sure that your terminal graphics work correctly (e.g. driver tracker).

```sh
docker run -it -e TERM_PROGRAM -v $HOME/pitwall/data:/data ziofil/pitwall

# Arguments can still be passed to the executable as normal
# for example:
docker run -it -v $HOME/pitwall/data:/data ziofil/pitwall import 2025
```

##### Known Issues with Docker

- Audio playback of Team Radio may not work when using Docker. This is due to difficulties in using audio devices in a cross-platform way, which I haven't managed to figure out yet.

#### Run directly from Source

```sh
# Checkout the git repository
git clone git@github.com:ziofil/pitwall.git
cd pitwall

# Run the console project with `dotnet run`
dotnet run --project Pitwall.Console/Pitwall.Console.csproj

# Arguments can be provided after the -- argument, for example
dotnet run --project Pitwall.Console/Pitwall.Console.csproj -- import 2025
```

### Start Timing for a Live Session

1. Start `pitwall` as described above
2. Navigate to the <kbd>S</kbd> `Session` Screen
3. Start a Live Session with the <kbd>L</kbd> `Start Live Session` action

During the session, streamed timing data will be written to [the configured data directory](#default-directories). This will allow for [future replays](#start-timing-for-a-pre-recorded-session) of this recorded data.

### Start Timing for a Pre-recorded Session

Data for pre-recorded sessions should be stored in the `<data-directory>/<session-name>` directory. Sample data can be found in this repos [Sample Data](/Sample%20Data/) folder. To use this sample data, copy one of the folders to [the configured data directory](#default-directories) and then it will be visible in step 4 below.

1. OPTIONAL: Download sample data to [the configured data directory](#default-directories). If you already have data, or have checked out the repository, skip to the next step.

    ```sh
    # Import data from the 2025 race in Suzuka
    pitwall import 2025 --meeting-key 1256 --session-key 10006
    ```

2. Start `pitwall` as described [above](#installation)
3. Navigate to the <kbd>S</kbd> `Session` Screen
4. Start a Simulated Session with the <kbd>F</kbd> `Start Simulation` action.
5. Select the session to start using the Up/Down arrows, then pressing <kbd>Enter</kbd>
6. Skip forward in time a bit by decreasing the delay with <kbd>N</kbd> (or <kbd>⇧ Shift</kbd> + <kbd>N</kbd> to decrease by 30 seconds).

### Download a previous session data for replay

F1 provides static timing data files for already completed sessions. This data can be downloaded and converted into the same format `pitwall` uses to save live recorded data. You can then replay the old session using the steps above.

1. List the meetings that have data available to import with `pitwall import <year>`
2. Review the list of meetings returned from the command, and list the available sessions inside the chosen meeting with `pitwall import <year> --meeting-key <meeting-key>`
3. Review the list of sessions, and select one to import: `pitwall import <year> --meeting-key <meeting-key> --session-key <session-key>`
4. Data that is imported will be saved to the configured data directory. See [Configuration](#configuration) for information on how to change this.

### During the Session

#### Managing Delay

All session data, whether live or pre-recorded, is sent to a `Channel` that acts as a delayed-queue. After your currently configured delay, data points are pulled from the queue and processed, leading to updates on the timing screens. The amount of this delay can be changed with the <kbd>M</kbd>/<kbd>N</kbd> `Delay` actions whilst on the timing screens. Hold <kbd>⇧ Shift</kbd> to change the delay by 30 seconds instead of 5. Use the <kbd>,</kbd>/<kbd>.</kbd> keys to change by 1 second. When using `pitwall` during a live session, you may wish to increase this delay to around ~50 seconds (actual number may vary) to match with the broadcast delay and avoid being spoiled about upcoming action.

Simulated sessions start with a calculated delay equal to the amount of time between the start of the actual session and now. This means you can decrease the delay with the <kbd>N</kbd> `Delay` action to fast-forward through the session.

Data processing, and therefore the "session clock" can be paused using the <kbd>P</kbd> `Pause Clock` action. This allows you to easily synchronize pre-recorded sessions by pausing the session in Pitwall, then resuming at the perfect time when, for example, the formation lap starts.

#### Using the Cursor

There is a global cursor that is controlled with the <kbd>▼</kbd>/<kbd>▲</kbd> `Cursor` actions. What this cursor does depends on the screen, for example it picks a team on the team list, or a radio message on the Comms page. Hold the <kbd>⇧ Shift</kbd> key to move the cursor by five positions instead of one.

## Configuration

Pitwall can be configured using either a simple `config.json` file, through the command line at startup, or using environment variables. JSON configuration will be loaded from [the appropriate config file path](#default-directories), if it exists.

A JSON Schema file is available in the root of this repository which can help when creating the `config.json` file. Add the JSON `{"$schema": "https://raw.githubusercontent.com/ziofil/pitwall/refs/heads/master/config.schema.json"}"` to your `config.json` to get automatic code complete and validation.

To view what configuration is currently being used, open the <kbd>I</kbd> `Info` screen when the app starts up.

| JSON Path                                     | Command Line                | Environment Variable                                      | Description                                                                                                                                                                                                                             |
| --------------------------------------------- | --------------------------- | --------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dataDirectory`                               | `--data-directory`          | `PITWALL_DATADIRECTORY`                                | The directory to which JSON timing data is read or written from. This directory is also where Whisper models will be stored (if downloaded) for team radio transcription.                                                               |
| `logDirectory`                                | `--log-directory`           | `PITWALL_LOGDIRECTORY`                                 | The directory to which logs are written to.                                                                                                                                                                                             |
| `verbose`                                     | `-v\|--verbose`             | `PITWALL_VERBOSE`                                      | Whether verbose logging should be enabled. Default: `false`. Values: `true` or `false`.                                                                                                                                                 |
| `apiEnabled`                                  | `--with-api`                | `PITWALL_APIENABLED`                                   | Whether the app should expose an API at <http://localhost:61937>. Default: `false`. Values: `true` or `false`.                                                                                                                          |
| `notify`                                      | `--notify`                  | `PITWALL_NOTIFY`                                       | Whether the app should sent audible BELs to your terminal when new race control messages are received. Default: `true`. Values: `true` or `false`.                                                                                      |
| `preferFfmpegPlayback`                        | `--prefer-ffmpeg`           | `PITWALL_PREFERFFMPEGPLAYBACK`                         | Prefer the usage of `ffplay` for playing Team Radio on Mac/Linux, instead of afplay/mpg123 respectively. `ffplay` is always used on Windows. Default: `false`. Values: `true` or `false`.                                               |
| `forceGraphicsProtocol`                       | `--force-graphics-protocol` | `PITWALL_FORCEGRAPHICSPROTOCOL`                        | Forces the usage of a particular graphics protocol instead of using heuristics to find a supported one. Values: `Kitty`, `Sixel`, or `iTerm`.                                                                                           |
| `preventDisplaySleep`                         | `--prevent-sleep`           | `PITWALL_PREVENTDISPLAYSLEEP`                          | If enabled, will attempt to prevent the device/display from sleeping whilst the app is running. Intended to help prevent accidental stream disconnection due to inactivity on your device. Default: `false`. Values: `true` or `false`. |
| `enablePositionSmoothing`                     | N/A                         | `PITWALL_ENABLEPOSITIONSMOOTHING`                      | Whether car positions on the track page glide between updates by fitting recent samples (the dot lags live slightly), rather than jumping to each raw sample. Default: `true`.  Values: `true` or `false`.                          |
| `formula1AccessToken`                         | N/A                         | `PITWALL_FORMULA1ACCESSTOKEN`                          | The access token to use when connecting to the F1 Live Timing feed. Only required to see additional data feeds.                                                                                                                         |
| `externalPlayerSync.enabled`                  | N/A                         | `PITWALL_EXTERNALPLAYERSYNC__ENABLED`                  | Whether synchronisation of the session clock is enabled. Default: `false`. Values: `true` or `false`.                                                                                                                                   |
| `externalPlayerSync.url`                      | N/A                         | `PITWALL_EXTERNALPLAYERSYNC__URL`                      | The URL to use when connecting to the external services websocket. For Kodi, this will be something like `ws://localhost:9090/jsonrpc`.                                                                                                 |
| `externalPlayerSync.serviceType`              | N/A                         | `PITWALL_EXTERNALPLAYERSYNC__SERVICETYPE`              | What type of service is behind the provided URL to synchronise with. Values: `Kodi`.                                                                                                                                                    |
| `externalPlayerSync.webSocketConnectInterval` | N/A                         | `PITWALL_EXTERNALPLAYERSYNC__WEBSOCKETCONNECTINTERVAL` | If the websocket connection fails or ends, how long to wait before trying again (in ms). Default: `30000`.                                                                                                                              |

### Default Directories

Pitwall tries to adhere the Windows and XDG standards as much as possible. By default, timing data and logs are written/read at the following directories:

| Type        | Windows                                | Linux/Mac                                  | Linux/Mac Fallback                  |
| ----------- | -------------------------------------- | ------------------------------------------ | ----------------------------------- |
| Config File | `$env:APPDATA/pitwall/config.json` | `$XDG_CONFIG_HOME/pitwall/config.json` | `~/.config/pitwall/config.json` |
| Data        | `$env:LOCALAPPDATA/pitwall/data`   | `$XDG_DATA_HOME/pitwall/data`          | `~/.local/share/pitwall/data`   |
| Logs        | `$env:LOCALAPPDATA/pitwall/logs`   | `$XDG_STATE_HOME/pitwall/logs`         | `~/.local/state/pitwall/logs`   |

Data and Logs paths can be configured as [described above](#configuration).
The config file location cannot be modified, and will always be read from the above location.

### F1 TV Account Login

Since the 2025 Dutch GP, the F1 Live Timing feed no longer freely publishes some of the data we need to provide all functionality in pitwall.

The following functionality is now only available to those who have an active F1 TV subscription:

- Car positions on the track page
- Car telemetry (speed, throttle, brake, gear, RPM, DRS)
- Pit Stop times (in box and in pitlane)

Importing a session after it finishes still fetches all the data, so all functionality is available if you use pitwall after a race finishes.

Linking your F1 TV account with pitwall is as simple as running `pitwall login` before starting your session, and following the instructions displayed. pitwall will open a WebView on to the F1 website, allowing you to sign in. It will then read the generated access token cookie for you from the WebView, and save it in the pitwall config file. This token is usually valid for a week, so you will likely have to perform this action often.

If you've already started a session, you can launch the login window using the <kbd>A</kbd> `Account` screen, then pressing <kbd>L</kbd> `Login`.

The current validity of your access token can be shown by running `pitwall info` (or in the Info screen of the TUI). A note will also be displayed on the entrypoint display of the TUI, reminding you to re-login if the current token has expired.

If the `pitwall login` command does not work for you, there is a manual process:

1. In a web browser, navigate to [the Formula 1 Account Login](https://account.formula1.com/#/en/login)
2. Log in with your account credentials
3. Open the browsers web inspector/inspect element/dev console
4. Navigate to the cookie storage section (usually in `Application` or `Storage`)
5. Copy the value of the `login-session` cookie stored under the `static.formula1.com` or `formula1.com` domain
6. Paste this string (without modification) in to the pitwall config file with the key `"formula1AccessToken"`

Example:

```json
{
  "$schema": "https://raw.githubusercontent.com/ziofil/pitwall/refs/heads/master/config.schema.json",
  "formula1AccessToken": "%7B%22data%22%3A%7B%22subscriptionToken%22%3A%22eyJ....."
}
```

## Logging

`Pitwall.Data` writes logs using the standard `ILogger` implementation. SignalR client logs are also passed to the standard `ILoggerProvider`.

When running `pitwall` logs are available in two places:

- Logs are stored in memory and viewable the <kbd>L</kbd> `Logs` screen. Logs can be scrolled on this screen, and the minimum level of logs shown can be changed with the <kbd>M</kbd> `Minimum Log Level` action.
- Log files are written to the [configured log directory](#default-directories).

Default log level is set to `Information`. More verbose logging can be enabled with the [`verbose` config option](#configuration).

## Alternate Key Binds

`pitwall` displays the keys associated with each action that is applicable for the current screen.
However, the shown keys aren't the only keys which can trigger the action, there are alternative keys which may be more intuitive for users of other TUIs.

- <kbd>H</kbd>,<kbd>J</kbd>,<kbd>K</kbd>,<kbd>L</kbd> can be used in place of arrow keys.
- <kbd>Backspace</kbd> can be used in place of the <kbd>Escape</kbd> key.
- <kbd>^ Control</kbd>+<kbd>C</kbd> can always be used to go back a screen, or exit the app. You may need to press multiple times to completely exit the app.
- <kbd>⇧ Shift</kbd> can be held for certain actions to modify the action. For example, hold shift to change delay by 30 seconds instead of 5, or to move the cursor by 5 positions instead of 1.

## Data Recording and Replay

All events received by the live timing client will be written to the configured `Data Directory`, see [see Configuration for details](#configuration). Files will be written to a subdirectory named using the current sessions name, e.g. `<data-directory>/2025_Jeddah_Race/`. In this directory, two files will be written:

- `subscribe.json` contains the data received at subscription time (i.e. when the live timing client connected to the stream)
- `live.jsonl` contains an append-log of every message received in the stream

Both of these files are required for future simulations/replays. The `IJsonTimingClient` supports loading these files and processing them in the same way live data would be. Data points will be replayed in real time, using an adjustable delay.

## API

pitwall ships with a simple HTTP API which allows for local integrations with your timing screen/data. [see Configuration for details on how to enable the API](#configuration). Once enabled, visit [the Swagger UI page at http://localhost:61937/swagger/index.html](http://localhost:61937/swagger/index.html) to see what APIs are available. This is an easy place to test the API and see what kind of data you get, and also the schema.

### Control API

The Control API `POST http://localhost:61937/control` allows you to issue control commands to the currently running session to, for example, pause the session clock.

```sh
# Pause the session clock
curl -H "content-type:application/json" -X POST http://localhost:61937/control -d '{"operation": "PauseClock"}'
# Resume the session clock
curl -H "content-type:application/json" -X POST http://localhost:61937/control -d '{"operation": "ResumeClock"}'
# Toggle (Play/Pause) the session clock
curl -H "content-type:application/json" -X POST http://localhost:61937/control -d '{"operation": "ToggleClock"}'
```

### Data API

The Data API `POST http://localhost:61938/data/<data-type>/latest` allow you to fetch the current raw timing data state. Take a look at the Swagger schema, and the [source model files](./Pitwall.Data/Models/TimingDataPoints/) to understand more about what data is available for each timing `data-type`.

## External Session Clock Sync

If you watch F1 using an app like Kodi, you can choose to connect pitwall to the Kodi API so that the pitwall's session clock automatically pauses when you pause the Kodi feed.
This allows you to keep your timing screen synchronised with your video feed.

See [Configuration](#configuration) for how to configure the `externalPlayerSync` options to enable this functionality.
Once configured, you can use the `Info` view in pitwall to confirm its correct, and see if the WebSocket successfully connected.
[Logs](#logging) are also output with information or any errors related to this functionality.

> [!NOTE]
> Currently this functionality is only supported for Kodi over its [WebSocket JSON RPC API](https://kodi.wiki/view/JSON-RPC_API#WebSocket).
> If you use a different video player, please raise a GitHub issue and we may be able to implement support!
> You can also make your own integrations with our [Control API](#control-api).

## Notice

Pitwall is unofficial and is not associated in any way with the Formula 1 companies. F1, FORMULA ONE, FORMULA 1, FIA FORMULA ONE WORLD CHAMPIONSHIP, GRAND PRIX and related marks are trade marks of Formula One Licensing B.V.

Pitwall is a modified version of [undercut-f1](https://github.com/JustAman62/undercut-f1), Copyright (C) Aman Dhoot.
Modifications Copyright (C) 2026 Filippo Miatto. The changes made in this fork are recorded in the git history of this repository.

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
as published by the Free Software Foundation, version 3. It is distributed WITHOUT ANY WARRANTY; see the [LICENSE](LICENSE) file for details.
