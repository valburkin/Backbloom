# Security policy

How to report a vulnerability in Backbloom, and which parts of the app matter most for security.

## Supported versions

Security fixes go into the latest release only; installed copies update themselves automatically.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://github.com/valburkin/Backbloom/security/advisories/new), not in a public
issue. Include the version, what an attacker can do, and the steps to reproduce it.

You'll get an answer within a week. Once a fix is released, the advisory is published with credit to you, unless you
prefer otherwise.

## Scope

Backbloom listens on `127.0.0.1` only until remote access is turned on; then other devices on the local network sign
in with a password the user chose. Issues of particular interest:

- access to the JSON-RPC API or the web UI from another device without the password, or from another origin;
- anything that makes the app listen on a network address without the user turning remote access on;
- the update path (packages are downloaded from this repository's GitHub releases);
- the handling of Wi-Fi credentials during the guided setup of a new LED controller.
