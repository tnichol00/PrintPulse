# PrintPulse 1.0.0 — release candidate

PrintPulse is a Windows 11 tray companion for monitoring Bambu Lab printers. Bambu Studio does not need to be running. Every account printer gets its own card; additional cards scroll vertically. This is an independent app, not an official Bambu Lab product.

## Download

Download `PrintPulse-win-x64.zip` from [GitHub Releases](https://github.com/tnichol00/PrintPulse/releases), extract it, and follow the instructions below.

## Run and sign in

1. Keep the entire `PrintPulse-win-x64` folder together in a stable location. Run `PrintPulse.exe`. No .NET or Visual Studio installation is needed for this portable build.
2. Find the blue PrintPulse icon in the Windows notification area. Windows may place it under the hidden-icons arrow. Left-click it to open or close the panel. Click elsewhere or press Escape to dismiss it; monitoring continues.
3. Right-click the icon and choose **Settings**. You can also run `PrintPulse.exe --settings`. Settings has a normal window while open; the background monitor has no taskbar button.
4. Choose Global or Mainland China and enter your Bambu account email. Use password sign-in or **Email me a code**. If Bambu requests verification, enter the emailed or authenticator code in the same secret field, then choose **Sign in**. Enter secrets only in the app, never in a chat or script.
5. All discovered printers appear automatically. The global status indicates whether cloud telemetry is connected. The first full report can take time. Settings lets you hide individual printers without removing them from the account.

If already running, launching another copy opens the existing panel. Choose **Open PrintPulse** from Settings to view it directly.

## Everyday behavior

- Right-click menu: Open PrintPulse, Refresh, Settings, Start with Windows, Sign out, Exit.
- Refresh restarts the account discovery and cloud connection. Reports update asynchronously. Ordinary percentage changes do not generate notifications.
- Printing, Preparing, Paused, Idle, Completed, Failed / Error and Offline have explicit card states. Missing fields show a dash. Unknown cloud fields never become fabricated values.
- A cloud outage preserves last-known cards and progress and shows a reconnect banner. Offline printers retain useful information. Active printers with no report for three minutes while cloud-connected are marked offline; this is a stale-telemetry heuristic.
- ETA derives from the last reported remaining minutes. Paused/offline states suppress ETA because the finish time is no longer reliable.
- Thumbnails are downloaded only when the cloud task identity matches the current job. Missing or unsupported images use a model placeholder. No historical task is substituted for an active job.

## Start with Windows

Turn on **Start with Windows** in Settings or the tray menu. This writes a current-user Windows startup entry pointing to this executable with `--startup`. It launches silently. If you move the portable folder, turn startup off and on again so Windows uses the new path. Leave it off if you do not want automatic launch.

## Notifications

Settings controls start, completion, failure and active-printer disconnect events separately. Native Windows toasts are used, with a native tray notification fallback if toast registration fails. Windows notification settings and Do Not Disturb can suppress delivery. Initial status discovery, progress updates, and pause/resume do not generate a burst of events. Notification clicks open the panel.

## Security and local files

Application data is stored in `%LOCALAPPDATA%\PrintPulse`:

- `session.bin`: the session token and account identity encrypted with Windows DPAPI for the current Windows user. Passwords and verification codes are never saved. Session writes use an encrypted temporary file followed by replacement.
- `settings.json`: notification preferences and hidden printer IDs; no authentication secrets.
- `thumbnails`: a bounded cache of up to 200 decoded images, downloaded over HTTPS from recognized Bambu/MakerWorld domains and the exact Bambu task bucket `or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com`. These images are not encrypted.
- `lifecycle.log`: created only with the optional `--diagnostics` launch flag. It contains startup stage names and timestamps only, not account data or network bodies.

**Sign out** cancels monitoring, invalidates in-flight authentication results, removes local authentication files and clears cards. It also attempts to clear thumbnails. It does not remotely revoke Bambu sessions on other devices. Never send `session.bin` or account tokens with bug reports. Other processes running as your Windows user may also use DPAPI; encryption is not a separate Windows-account boundary.

## Troubleshooting

**No tray icon:** check the hidden-icons arrow. A second launch activates the existing panel rather than running duplicate monitors. Use `--settings` to open Settings in the existing instance if finding the tray icon is difficult.

**Cloud disconnected:** the app retries with exponential backoff and jitter, capped at about two minutes. Check connectivity and account region. Port 8883/TLS is used for MQTT; HTTPS uses port 443. Refresh retries immediately. Reports remain visible during reconnection.

**Session expired/rejected:** open Settings and sign in again. There is no assumed undocumented refresh-token contract; reauthentication is the deliberate recovery path. Saved passwords are never replayed.

**Sign-in blocked/403:** Bambu's community-observed account API can change or require browser verification. Try email-code sign-in. The app does not bypass CAPTCHA, CSRF protection or service access controls. Social-provider-only login and browser challenge automation are not implemented. If Bambu refuses the supported methods, live integration remains blocked pending a supported user login mechanism.

**No printers or missing thumbnails:** confirm the account is bound to the printers and they can connect to Bambu Cloud. LAN-only printers are outside this version's scope. Some locally submitted jobs have no cloud thumbnail. No camera, printer controls, FTP access, or slicing is implemented.

**Secure storage unavailable:** run under your ordinary Windows account with a loaded user profile. DPAPI may fail under an isolated impersonated test profile. The app refuses to save an unencrypted session.

**Startup or settings cannot save:** verify access to your current-user registry and application data folder. Errors are surfaced without logging secrets.

**App closes unexpectedly:** use `--diagnostics` to collect startup stage names. The build is unsigned; code signing and an installer are not included. Do not disable Windows protections to run it.

## Build from source

Requires Windows 11 and .NET 8 SDK. From this source directory:

```powershell
dotnet build PrintPulse.sln -c Release
dotnet run --project PrintPulse.Tests -c Release -- ..\PrintPulse-test-evidence
dotnet publish PrintPulse.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o ..\PrintPulse-win-x64
```

`Build.ps1` combines build, tests and publishing. Tests are a separate executable using isolated fixture secrets and transport responses. The release application does not contain a demo switch or sample printers. WPF raster tests check 100/125/150/200% rendering density; they do not simulate changing Windows display settings.

The test executable also supports an explicitly opt-in `--live-readonly <evidence-folder>` diagnostic. It reads the real saved session, connects for 75 seconds using the production monitor, and saves a view rendering and printer-status summary. Run this only when you authorize access to your private Bambu account data. It does not print tokens, account email or printer serial identifiers, and performs no printer-control actions. Its output can contain private printer/job names. The ordinary test command above uses fixtures only.

## Architecture and limitations

- `PrintPulse.Core`: tolerant per-printer state reducer, authentication/discovery REST adapter, TLS MQTT connection loop and event policy.
- `PrintPulse.App`: WPF flyout and Settings, Windows NotifyIcon, native toasts, DPAPI, preferences, startup registration and image cache.
- `PrintPulse.Tests`: isolated state, security, HTTP, cancellation, reconnect and WPF layout tests. The ordinary test run does not sign into a real Bambu account; the opt-in live diagnostic is described above.

Cloud interfaces are unofficial/community-observed and not a vendor-supported public API. Implementation-time research is recorded in `PROTOCOL-RESEARCH.md`. Real A1 telemetry and the matching current print thumbnail were verified using the production monitor. Discovery refreshes every five minutes, task metadata every minute (latest 100 account tasks), while progress comes through MQTT. Snapshot requests are throttled within each connection run; pressing Refresh starts a new run. No persistent status cache survives an app restart. Windows theme/high contrast adaptation, code signing, installer/update service, browser/social login and LAN-only monitoring are intentionally deferred. Light-theme surface is the supplied visual target.

Final release approval requires the independent grader's score of at least 8.0/10 and every critical gate passing. The current build must not be called production-approved solely because it compiles or fixture tests pass.
