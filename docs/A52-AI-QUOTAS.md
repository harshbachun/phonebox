# A52 AI quota update · November 23, 2026

The deployed Windows agent is `0.9.24-a52.quotas.1` at `2629cbb`, based on A52 commit `b45081b`. The phone continues to run `0.9.24-a52`. This is the quota-only backport chosen after comparing the broader `a52-0.9.29` build.

## What changed

- The agent can read account-wide Codex limits from the signed-in CLI while an enabled quota widget includes Codex. A successful read is normally refreshed every three minutes. Failed reads back off and the existing local session reader remains available. Windows can resolve the Codex desktop package when the CLI is missing from the sign-in PATH.
- The CodexBar integration keeps recent healthy readings through temporary errors, discards expired windows from held readings, and polls again on manual refresh. The data reader, selection and cache handling come from the official 0.9.29 source. [Official quota monitor](https://github.com/ersinkoc/phonebox/blob/v0.9.29/agent/PhoneBox.Agent/QuotaMonitor.cs)
- **Settings → General → AI quotas & CodexBar → Read live Codex quotas** controls polling. The installed profile sets `codexBar.codexLiveQuota` to `true`. This was the only saved setting changed by the installation.
- The existing Android widget displays the new measurements through the same quota endpoint. Its 12 × 6 bar layout, colors, labels, reset line and pace markers use the original A52 renderer.

## Installation and verification

The Windows x64 publish contained seven files. Only `PhoneBoxAgent.exe` differed from the previous installation. The installer backed up the executable and private profile, replaced the executable, enabled polling and restarted the agent. The phone kept its installed APK.

| Check | Result |
|---|---|
| Account quota | The installed agent's `/api/quotas` reported `codex-app-server` and two measured windows, 5-hour and Weekly. |
| Reader gating | An enabled Codex quota page passed. Hidden and Claude-only pages skipped the live reader. |
| Layout and assets | All ten served page configurations matched the pre-install snapshot. All 24 served panel files were byte-identical to their pre-install versions. Every other saved profile value matched. |
| Custom features | The Discord and VALORANT status endpoints both responded. The original Discord page and VALORANT widget remained configured. |
| Device | The A52 reconnected on Wi-Fi during the restart. Restarting the stalled ADB listener restored USB. The agent then reported Android version `0.9.24-a52` and automatic updates off. |
| Installed executable | SHA-256 `884321AD20AE4A44C895CC5FEEBB009B2417C08DD561C0BB4EEBAE4151C0AD87`. |

Verification used the live agent API and compared the served assets. A new Discord call or VALORANT match was outside this quota-only installation.

## Local recovery

The private backups remain on the installation PC. The previous executable is `%LOCALAPPDATA%\PhoneBox\PhoneBoxAgent.before-quotas-20261123-202801.exe`. The settings backup is `%APPDATA%\PhoneBox\config.before-quotas-20261123-202801.json`. Turning off **Read live Codex quotas** switches quota selection back to the previous local/CodexBar path.
