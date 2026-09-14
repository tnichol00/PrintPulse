# Protocol research — 12 September 2026

Research preceded the cloud implementation. This records observed interfaces, not a claim that the current user's account has been authenticated. Live verification requires the user to sign in through PrintPulse.

## Primary implementation sources inspected

- Maintainer implementation of account login, email/TFA, token identity fallback and printer/task retrieval: https://github.com/greghesp/ha-bambulab/blob/main/custom_components/bambu_lab/pybambu/bambu_cloud.py
- Endpoint and printer-state definitions: https://github.com/greghesp/ha-bambulab/blob/main/custom_components/bambu_lab/pybambu/const.py
- Recent login regression discussion: https://github.com/greghesp/ha-bambulab/issues/2056
- Protocol author's MQTT observations: https://github.com/Doridian/OpenBambuAPI/blob/main/mqtt.md
- Protocol author's HTTP observations: https://github.com/Doridian/OpenBambuAPI/blob/main/cloud-http.md
- MQTTnet library implementation/documentation: https://github.com/dotnet/MQTTnet and https://dotnet.github.io/MQTTnet/
- Supported Windows rounded-corner API: https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners

## Implementation decisions

Use regional Bambu API hosts for account login and discovery, and regional TLS MQTT brokers for telemetry. Handle password login, emailed codes, and authenticator challenges as separate steps. Derive the MQTT account name from token claims when present; opaque tokens require the authenticated preference endpoint. Subscribe separately to each printer's report topic, and merge partial reports. Thumbnail metadata must match the current task identity before use.

The recent maintainer code reports CSRF/browser protection on website-hosted TFA. PrintPulse surfaces failures and offers email-code sign-in. It identifies itself as PrintPulse; it does not impersonate another application's client identifier or bypass access controls. HTTP headers and account restrictions therefore need live confirmation.

Full status requests are monitoring-only `pushing.pushall` messages, throttled to avoid aggressive refresh on P1-series devices. No motion, print control, slicing, upload or firmware commands are implemented. A broker connection and periodic account discovery are independent of Bambu Studio.

Session expiry deliberately asks for reauthentication rather than assuming an older refresh contract still works. Real-account success and hardware telemetry remain separate acceptance items. Check the grade report and acceptance evidence before treating this build as approved.
