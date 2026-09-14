# Historical acceptance evidence — 13 September 2026

This table describes an earlier build, before the final thumbnail fix. Current validation and remaining limitations are summarized in README.md. It is not independent approval of the current package.

Evidence applies to the production App DLL SHA-256 `3ACE87E68470E98CB3492734D72B77E34A1813A7761C930A037F941155C06CC2` and Core DLL `6FE2ABD1DA4EAD257E2A6451DB5043F51037BACCF8F381020F670E7C7DBB9234`.

| Scenario | Evidence |
|---|---|
| Build/package launch | Self-contained Windows x64 build; zero build warnings/errors; actual Settings window observed. |
| Tray toggle, dismiss, context menu | User explicitly performed left-click open/close, click-away dismissal and right-click menu checks and confirmed all three work. This was not automated by the grader. |
| One real printer | Explicitly authorized live check uses the exact production assemblies and saved encrypted session. A1 printing data, job name, percentage, layers, remaining time and ETA rendered correctly; multiple distinct reports observed. |
| Three printers / independent updates | Exact production assemblies tested with isolated fixtures; three cards stack and updates do not leak across printers. |
| Overflow | Twelve fixture cards remain in a scrollable card area beneath the fixed header. |
| Idle, offline, paused, failed, long names | Reducer tests and WPF renders passed; metadata preserved on partial updates and offline transitions; names ellipsize. |
| Missing thumbnail | Clean model placeholder observed in live and fixture views; no broken-image icon. |
| Secure persistence / sign out | Independent current-user DPAPI encryption/decryption, corrupt-file tolerance, deletion and stale-login/expired-run cleanup tests passed. Real saved-session restoration observed; actual user session was retained. |
| Network loss, retry, responsiveness | Controlled HTTP failures, pending requests, cancellation and expired sessions tested. Dispatcher remained responsive and automatic retries were bounded. User network was not deliberately disconnected. |
| Settings identity regression | Independent synthetic test verified initial printer name/model and subsequent rename updates; authorized live Settings inspection confirmed A1. |
| DPI | Production WPF view rendered and inspected at 100/125/150/200% density. Actual Windows scaling changes and multi-monitor transitions were not exercised. |
| Startup | Start-with-Windows setting was observed enabled and implementation reviewed. A real Windows logon/restart was not performed. |
| Native notifications | Meaningful-event selection, suppression of percentage/resume/discovery spam and disabled preferences tested. Native toast delivery under the user's Windows notification settings was not exercised. |
| Dependencies | Current NuGet vulnerability audit including transitive packages reported no vulnerable packages. |
| Performance | Short connected-process sample: approximately 1.28% of one logical CPU core, about 166 MB working set. This is a short observation, not a long-duration soak test. |

Independent grading reports apply to earlier builds; no final independent score is claimed for this package. Private live evidence is not distributed. Tests do not print passwords, verification codes, tokens or account email. Live evidence can contain private printer/job names and should be shared deliberately.
