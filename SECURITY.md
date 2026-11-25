# Security policy

## Supported versions

Only the latest release receives security fixes. Please update before reporting.

## Reporting a vulnerability

Please report security issues **privately**, through GitHub:
**Security** tab → **Report a vulnerability** (private vulnerability reporting).
Don't open a public issue for anything that could put users at risk.

Helpful details: the version, Windows build, what an attacker needs (a crafted SD card or card image,
a program running as the user, another local account, …), and steps to reproduce. Never include real
camera footage, card images from real cameras, or real SD-card passphrases — a synthetic image
(see `tools/make-fixture.sh`) is ideal.

## Scope

The app runs as administrator to read raw SD cards, so the main things we care about are:

- anything a crafted card or image can do (code execution, writes to the card, hangs);
- anything an unelevated program can do to get administrator rights through the app;
- anything that exposes footage, metadata or the passphrase beyond what the app shows, including
  through the optional AI-agent access (named pipe, loopback media server, MCP server).

Known, documented limits are listed in the README under *Security model*.
