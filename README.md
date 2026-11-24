# IndependentAirbrake (Nuclear Option)

BepInEx 5 mod that gives the airbrake its own control, so it works at any throttle
(vanilla only deploys it when the throttle is pulled to 0).

## Features
- Airbrake on a keyboard key and/or any joystick button, in **Hold** or **Toggle** mode.
- Press **F8** in game, then the joystick button you want, to bind it (Esc cancels).
- HUD throttle gauge shows `AIRBRAKE` while the brake is deployed, like vanilla.
- `AirbrakeAtIdleThrottle` (default off) keeps the vanilla throttle-0 behaviour as well.
- Adds `+IndependentAirbrake` to the game version so modded clients only match each other online.

## Install
Build with `dotnet build -c Release` (the .csproj references the game's DLLs from the default
Steam install path) and copy `IndependentAirbrake.dll` to
`Nuclear Option/BepInEx/plugins/IndependentAirbrake/`.

Settings: `BepInEx/config/com.local.independentairbrake.cfg`.
