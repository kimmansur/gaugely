# Security

Gaugely reads credentials, so it is fair to want to know exactly what it does with them. This
file describes the fork as shipped; most of it is inherited from
[RateTray](https://github.com/nowrap/rate-tray/blob/main/SECURITY.md), with the differences called
out.

## What this app touches

**Files read**

| Path | Why | Written? |
|---|---|---|
| `%USERPROFILE%\.claude\.credentials.json` | OAuth access token for the Claude usage request | Only with `claude.autoRefreshToken` enabled — see below |
| `%USERPROFILE%\.codex\auth.json` | `exp` claim of the access token, to report sign-in validity | Never |
| `%APPDATA%\Gaugely\settings.json` | This app's own configuration | Yes |
| `%APPDATA%\Gaugely\cache.json` | Last readings, so a restart shows numbers at once | Yes |
| `%APPDATA%\RateTray\settings.json` | Copied once into the folder above on first start, if present | Never |

The copied file goes through the same `Normalize()` as any other settings file, so FORK-1 and
FORK-2 apply to it. Opt-ins that were on before — `autoRefreshToken`, for instance — stay on.

`cache.json` holds only values — limit ids, percentages, reset times, plan names. No token or
credential ever reaches it.

**Windows Credential Manager**

The Kimi Code and OpenRouter API keys are stored there, under `Gaugely/kimi` and
`Gaugely/openrouter`, never in `settings.json`. Keys saved by earlier builds under
`RateTray-Nox/…` are read once and copied to the new names; the old entry is left in place so
the earlier build keeps working. Removing a key in Settings removes both, and a failed removal is
reported instead of passing silently. If an earlier build writes the old entry again, it is picked
up again.

A **Start with Windows** entry from RateTray is moved over only when it launches `RateTray.exe` by a
fully qualified path — quoted, or without spaces, since Windows itself reads an unquoted
`C:\Program Files\…` as `C:\Program` plus arguments — and never replaces an existing Gaugely entry.

**Network** — every destination is a constant in the code, not a setting:

| Destination | When | What is sent |
|---|---|---|
| `https://api.anthropic.com/api/oauth/usage` | Claude enabled | The Claude OAuth token |
| `https://console.anthropic.com/v1/oauth/token` | Only with `autoRefreshToken`, when the token has expired | The refresh token |
| `https://api.kimi.com/coding/v1/usages` | A Kimi key is saved | The Kimi key |
| `https://openrouter.ai/api/v1/credits`, `/key` | An OpenRouter key is saved | The OpenRouter key |
| `https://api.github.com/repos/kimmansur/gaugely/…` | Update check (off by default) or the About buttons | Nothing — no token, no identifier |

Codex and Antigravity involve no network access from this app: it starts the local
`codex app-server` and `agy` processes and reads their output. What those do is their own
behaviour.

There is no telemetry and no crash reporting.

**Processes started**

- `codex.exe app-server`, once per poll, killed afterwards.
- `agy.exe -p /usage`, from its fixed install path under `%LOCALAPPDATA%\agy\bin`, with its own
  auto-updater disabled for that call.

## Differences from RateTray

Upstream treats `settings.json` as a legitimate place to configure endpoints and executables. On a
work machine that file is an attack surface: whoever can write it could redirect a token or make
the app launch another program. The fork closes three paths, all enforced in `Normalize()`, which
every loaded configuration passes through:

- **FORK-1** — `claude.usageUrl` and `claude.tokenUrl` keep their official host. A different host is
  replaced by the default; a different path on the official host is still accepted, so a moved
  endpoint can be followed without a rebuild.
- **FORK-2** — `codex.executablePath` is ignored. `codex.exe` is found in its known install
  locations and on `PATH`, never at a path read from the settings file.
- **FORK-3** — `claude.autoRefreshToken` is available as an opt-in, because people who use the
  Claude desktop app rather than the CLI have nothing else keeping the token fresh. The refresh
  only runs with an expired token, writes the file atomically, and — through FORK-1 — can only go
  to the official host.

## Updates

The update check is off by default. When enabled in About, it asks the GitHub API for this
repository's latest release once a day. A newer release produces a notification; nothing is
downloaded until you press **Download and install**.

The installer then:

1. accepts only assets under `https://api.github.com/repos/kimmansur/gaugely/releases/assets/`;
2. downloads the executable into memory and compares its SHA256 with the release's
   `SHA256SUMS.txt` before anything touches the disk;
3. writes it under a random name that must not already exist, and refuses it unless the version
   inside the file equals the release tag — so an old binary cannot be republished under a new
   number;
4. swaps it in with a single `ReplaceFile` call, keeping the previous version as `.old` until the
   new one starts. The executable's metadata — its ACL included — is carried over, and a failure to
   carry it over aborts the swap instead of being ignored.

**Limit, stated plainly:** the checksum is published in the same release as the binary. It proves
the file was not altered on the way, not who published it. Someone in control of this GitHub
account could publish a matching pair. That is why installing needs a click, and why a release is
only built by the tag-triggered workflow, with every third-party action pinned to a commit.

## Reporting a vulnerability

Please use GitHub's [private vulnerability reporting](https://github.com/kimmansur/gaugely/security/advisories/new)
rather than a public issue. If that is not possible, open a normal issue with only enough detail
to make contact, and it will be moved somewhere private.

This is a spare-time project: there is no bounty and no guaranteed timeline, but credible reports
are taken seriously and credited unless you would rather not be.

## Scope

In scope: credential handling, the settings file, the update installer, anything the app writes
or transmits, and process launching.

Out of scope: vulnerabilities in Claude Code, the Codex CLI, `agy`, or the services' own APIs —
report those to their vendors. Issues that exist identically in RateTray are best reported
upstream as well.
