# AetherFrame Community Desk

Rebuild started October 4, 2026, at the owner's request. This document is the first saved checkpoint. The dashboard is not implemented, reviewed or deployed yet.

## Hosting

Use the existing DigitalOcean droplet and ASP.NET Core server, behind its existing Caddy proxy, at `https://plates.aetherframe.dev/admin/`. This shares the deployment and database and requires no new hosting subscription. Availability is shared with the plugin's sharing service. Do not change DNS, production configuration, credentials, MinimumPlugin or LodestoneRelay as part of implementation.

## Scope

- A responsive, polished graphite and violet dashboard, with light and dark themes.
- Overview of published Plates, unresolved reports and server health, using actual stored data. No invented online count or new player telemetry.
- Review reports and current published content, dismiss reports, hide a shared Plate, and restore it. Require reasons and confirmation for moderation actions.
- Preserve the player's local content and binding. A moderation hold survives republishing but is not an account ban.
- One configured owner and explicitly approved moderators, identified by immutable numeric GitHub account IDs. Only the owner manages staff.
- GitHub OAuth, secure staff sessions, immediate revocation, antiforgery protection and transactional audit records. No repository or email scopes, saved provider tokens, default password or developer login.
- No raw database, deploy, secrets, billing or shell controls.
- Moderation audit entries retained for 30 days. No copies of removed content, reporter identities or player activity logs in the dashboard audit.

## Checkpoints and acceptance

1. Save this plan on an isolated branch and verify it through GitHub.
2. Save the authentication and storage foundation; keep the feature disabled by default.
3. Save the working interface and its API integration.
4. Add meaningful security, persistence and browser checks. Rebuild and test the new code; the previous lost implementation's results do not apply.
5. Open a draft PR naming the exact tested commit and every remaining limitation. Update the roadmap in the same PR. Security and correctness reviews and exact-head CI must pass before readiness or merge.
6. Production activation remains a separate step. The owner creates/configures the OAuth application and secrets. Do not activate or deploy through this task.

No checkpoint is considered saved until its remote file or commit has been read back. Upload in small batches and stop to resolve a failed checkpoint before continuing implementation.

## Design status

The owner authorized building the dashboard. The detailed authentication, moderation persistence, audit retention and rollout design is proposed by GPT pending independent review. Existing confirmed privacy requirements and the plugin's release gates remain in force. A browser rendering of a Plate must clearly state any differences from the in-game renderer.
