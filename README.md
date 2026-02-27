![Mâlie Logo](Screenshots/malie.png?raw=true)

# Mâlie

Mâlie is a Windows 11 live wallpaper desktop app.

It uses:
- **Three.js** for local, procedural isometric scene rendering.
- **WeatherAPI** for live weather/time context.
- **LatLng** for POI discovery.
- **Meshy API** for optional POI GLB generation/import/export.
- **Angular + PrimeNG** for the settings UI shell.

## Project Structure

- `App.xaml.cs`: desktop host lifecycle and wallpaper orchestration.
- `Windows/SettingsWindow.xaml.cs`: WebView host bridge for Angular UI.
- `www/`: wallpaper renderer (`app.js`, `styles.css`, Three.js vendor files).
- `ui-shell/`: Angular + PrimeNG UI.
- `Services/`: WeatherAPI, LatLng, Meshy, and scene directive services.
- `installer/`: Inno Setup and WiX assets.

## Requirements

- Windows 11
- .NET 8 SDK
- Node.js + npm
- WebView2 Runtime
- API keys:
  - WeatherAPI
  - Meshy
  - LatLng (server key)

## API Key Links

- WeatherAPI signup: https://www.weatherapi.com/signup.aspx
- WeatherAPI docs: https://www.weatherapi.com/docs/
- Meshy docs: https://docs.meshy.ai/en
- Meshy referral: https://www.meshy.ai/?utm_source=meshy&utm_medium=referral-program&utm_content=ZEU7XX&share_type=invite-friends
- Meshy referral program: https://www.meshy.ai/referral
- LatLng: https://www.latlng.work/
- LatLng docs: https://www.latlng.work/docs

## Run (Dev)

From repo root:

```powershell
# Build Angular shell
cd ui-shell
npm install
npm run build
cd ..

# Run desktop app
dotnet run --project .\Malie.csproj
```

## Publish (Win11)

```powershell
.\publish-win11.ps1
```

## Build EXE + MSI Installers

```powershell
.\build-installers.ps1 -Runtime win-x64 -Version 1.0.0 -AcceptWixEula
```

Notes:
- EXE is built with Inno Setup (`ISCC.exe`).
- MSI is built with WiX v7 (`wix.exe`).
- If WiX EULA is not accepted, run:

```powershell
wix eula accept wix7
```

FireGiant OSMF docs: https://docs.firegiant.com/wix/osmf/

## Runtime Notes

- Closing the settings window does not fully exit the app.
- The app minimizes to tray; right-click tray icon and choose **Exit** to quit.
- Debug logs are written to:
  - `%LOCALAPPDATA%\Malie\logs\app-debug.log`

## Credits

- Weather icons by Bas Milius: https://github.com/basmilius/weather-icons
- Weather data by WeatherAPI: https://www.weatherapi.com/
