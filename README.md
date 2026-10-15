# Simple Bike Trainer

Simple Bike Trainer rides Intervals.icu workouts on a Wahoo KICKR, shifts virtual gears with a Zwift Click, records each ride as a FIT file, and uploads it to Intervals.icu and Garmin Connect. It also generates rolling routes with a set length and amount of climbing. `simple-bike-trainer` is the desktop app; `simple-bike-trainer-cli` is the terminal version, with Bluetooth diagnostics.

| | Light | Dark |
| --- | --- | --- |
| Home | ![Home, light](docs/screenshots/home-light.png) | ![Home, dark](docs/screenshots/home-dark.png) |
| Ride | ![Ride, light](docs/screenshots/ride-light.png) | ![Ride, dark](docs/screenshots/ride-dark.png) |
| Summary | ![Summary, light](docs/screenshots/summary-light.png) | ![Summary, dark](docs/screenshots/summary-dark.png) |
| History | ![History, light](docs/screenshots/history-light.png) | ![History, dark](docs/screenshots/history-dark.png) |
| Settings | ![Settings, light](docs/screenshots/settings-light.png) | ![Settings, dark](docs/screenshots/settings-dark.png) |

## Install

Each release on the repository's Releases page has one download per platform, plus `SHA256SUMS`.

| System | Download |
| --- | --- |
| Windows | `simple-bike-trainer-win-x64.exe`, `simple-bike-trainer-win-arm64.exe` |
| Linux | `simple-bike-trainer-linux-x64`, `simple-bike-trainer-linux-arm64` |
| macOS | `simple-bike-trainer-osx-arm64.zip` (Apple silicon), `simple-bike-trainer-osx-x64.zip` (Intel) |

## Setup

Get an Intervals.icu API key under Settings > Developer Settings in Intervals.icu, then enter it in the app's Settings.

| What | Where |
| --- | --- |
| Settings | `~/.config/simple-bike-trainer/config.json`, mode 0600. `INTERVALS_API_KEY` and `INTERVALS_ATHLETE_ID` override the saved values. |
| Garmin session tokens | `~/.config/simple-bike-trainer/garmin/garmin_tokens.json`, mode 0600 |
| Rides | `~/.local/share/simple-bike-trainer/rides/` |

## Riding

The home page lists the next 7 days of planned rides from Intervals.icu, plus a free ride, a generated route, and a ramp FTP test. The first Connect scans for devices and lets you pick a trainer, heart rate monitor, and Zwift controllers; later connects reuse the choice. The `?` button or F1 explains every number on the ride page.

| Input | ERG step | Free ride or ERG off |
| --- | --- | --- |
| Click `+` (or `Y`), `+` or `↑` | target +5 W | shift up a gear |
| Click `-` (or `B`), `-` or `↓` | target −5 W | shift down a gear |
| `←` / `→` | | grade −/+ 0.5% |
| `e` | switch to gears | switch back to ERG |
| `s`, `l`, `p` or space | skip step, lap, pause | |
| `x` | end the ramp test | |
| `q` twice | finish the ride | |

The ride timer starts on the first pedal stroke and pauses after 3 seconds without power or cadence. Every 5 minutes the app saves a checkpoint; after a crash, the home page offers to save the unfinished ride.

## Uploads

| Destination | Setup |
| --- | --- |
| Intervals.icu | API key. Uploads link the ride to its planned workout. |
| Garmin Connect | Email, password, and MFA code in Settings, or `simple-bike-trainer-cli connect garmin`. Garmin forwards the ride to Strava and other linked apps. |

## Terminal app

The examples write `simple-bike-trainer-cli` for `dotnet run --project src/SimpleBikeTrainer.Cli --`.

```sh
simple-bike-trainer-cli --help
simple-bike-trainer-cli ride --event 91234567           # a planned ride
simple-bike-trainer-cli ride --route 30 --climb 400     # a 30 km loop with 400 m of climbing
simple-bike-trainer-cli ride --simulate --offline --file workout.txt
simple-bike-trainer-cli scan                            # list and save devices
simple-bike-trainer-cli probe                           # print trainer data and Click presses
```

## Development

Install the .NET 10 SDK.

```sh
dotnet run --project src/SimpleBikeTrainer.Desktop
dotnet test --solution SimpleBikeTrainer.slnx
dotnet tool restore && DOTNET_ROLL_FORWARD=Major dotnet xstyler --recursive --directory . --passive
```

The last command checks XAML formatting with [XamlStyler](https://github.com/Xavalon/XamlStyler); without `--passive` it rewrites the files. [tests/SimpleBikeTrainer.Desktop.Tests](tests/SimpleBikeTrainer.Desktop.Tests) drives every page headlessly with simulated devices and saves screenshots to `$SIMPLE_BIKE_TRAINER_SCREENSHOTS`; the images above come from it.

| Project | Contents |
| --- | --- |
| [src/SimpleBikeTrainer.Core](src/SimpleBikeTrainer.Core) | Bluetooth devices, workouts, the ride loop, metrics, FIT recording, and the Intervals.icu and Garmin clients |
| [src/SimpleBikeTrainer.Desktop](src/SimpleBikeTrainer.Desktop) | The Avalonia app |
| [src/SimpleBikeTrainer.Cli](src/SimpleBikeTrainer.Cli) | The terminal app and the `scan` and `probe` diagnostics |


## License

[GPL-3.0-only](LICENSE). [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists the projects parts of this code are based on.
