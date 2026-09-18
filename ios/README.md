# PrintPulse for iPhone

The app opens **Settings and Bambu sign-in only**. Print status lives in its WidgetKit home-screen widgets. Tapping a widget opens Settings; its refresh button requests an update without opening the app.

- Small and medium widgets display one printer; a large widget displays up to three.
- Edit a widget to choose a printer, or leave the choice empty to use the visible-printer list.
- Printer cards display job name, real reported percentage, layers, remaining time, ETA, and the matching cloud preview. Transparent previews replace the placeholder entirely.
- Each card labels the time of its last printer report. There is no invented progress or completion between reports.
- Global and Mainland China accounts support password, email-code, and authenticator challenge sign-in. Browser/CAPTCHA challenges are not bypassed.
- Authentication is stored in a shared iOS Keychain access group, accessible after first unlock on this device only. App Group storage contains snapshots and thumbnails, never passwords or tokens. Sign-out deletes local credentials and cached snapshots.

## Refresh behavior

The widget requests a timeline refresh after 15 minutes. iOS decides when to run it and may delay it. A bounded MQTT connection obtains a fresh printer snapshot during a refresh; it does not stay connected in the background. Opening Settings and tapping the widget refresh button also request an update. Offline or cloud failures preserve last-known data with its original report time. No always-on monitoring, push server, print controls, or background completion notifications are included.

## GitHub builds

`iPhone app and widget checks` builds and tests on GitHub's macOS runners, captures Settings and widget-card renders, and produces an **unsigned Xcode archive**. This archive is not an installable IPA.

`Build signed PrintPulse for iPhone` follows the PiStick build pattern: XcodeGen, Fastlane, encrypted Match signing storage, Apple API authentication, and TestFlight. It also supports exporting an Ad Hoc IPA for registered devices. Both the app and widget need signing profiles with the shared App Group. The workflow validates its signing configuration before proceeding. Signing secrets are never stored in this repository, and the workflow does not revoke existing certificates.

Apple signing is not connected yet. A signed IPA and real iPhone/widget validation remain pending. Existing Windows releases remain separate.

## Verification

Automated checks cover partial telemetry updates, printer independence, task-specific preview selection, old-image removal, hostname restrictions, state transitions, bounds, and SwiftUI card rendering. Unit tests use isolated fixtures only; gallery examples never enter a real widget timeline. The new iOS Bambu connection, Keychain sharing, widget refresh timing, and installation still require signed-device validation.

## Dependencies and references

- CocoaMQTT 2.4.1 — upstream LICENSE offers EPL 1.0 / EDL 1.0; used under EDL 1.0. Swift Package Manager resolves its socket dependencies. Notices ship in the app resources.
- [Apple: keeping widgets up to date](https://developer.apple.com/documentation/widgetkit/keeping-a-widget-up-to-date)
- [Apple: Keychain sharing](https://developer.apple.com/documentation/security/sharing-access-to-keychain-items-among-a-collection-of-apps)
- [Fastlane: multiple-target signing](https://docs.fastlane.tools/actions/match/)

This independent app uses community-observed Bambu Cloud interfaces, which may change. No Bambu credentials are sent to GitHub or an application backend.
