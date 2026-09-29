# Security Policy

## Supported versions

Security fixes go into the latest release only. Please upgrade to the newest version on the
[releases page](https://github.com/spenceclark/Vessel/releases) before reporting.

## Reporting a vulnerability

Please report vulnerabilities privately through
[GitHub's private vulnerability reporting](https://github.com/spenceclark/Vessel/security/advisories/new),
not in a public issue.

Include the Vessel version, your platform, and steps to reproduce. Vessel is maintained by
one person, so replies are best effort; expect an acknowledgement within a week. Confirmed
issues are fixed in a new release and published as a GitHub security advisory, crediting
you unless you prefer otherwise.

## Scope

Vessel stores prompts and responses locally and has no login by design. See
[PRIVACY.md](PRIVACY.md) for what it stores and who can reach it. These are expected
behaviour, not vulnerabilities:

- Anyone who can reach Vessel's listen address can read captured traffic through the UI,
  API or MCP endpoint. It binds `127.0.0.1` by default and warns when bound elsewhere.
- Any MCP client you connect can read captured traffic.
- Anyone with access to `vessel.db` can read the captured prompts and responses.

In scope, for example:

- A secret header stored unredacted in `vessel.db`, or an API key written to disk.
- A web page in your browser reading from or changing Vessel's API despite the Host and
  Origin checks.
- Captured data sent anywhere other than the backend the request was addressed to.
- Traffic reaching a backend modified in ways other than those documented.
