# AetherFrame Community Desk

Rebuilt October 4, 2026, at the owner's request, on `codex/admin-dashboard-rebuild` (PR #133). Disabled by default. Independent reviews, browser acceptance and CI passed, and GPT approved the design for merge and a controlled deployment. Nothing is deployed or activated by the PR; live checks follow the owner's activation.

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

The owner authorized building the dashboard. The detailed authentication, moderation persistence, audit retention and rollout design is APPROVED by GPT (October 4, 2026), with its accepted limitations recorded in the [decision register](DecisionRegister.md#community-desk-owner-and-moderator-dashboard-october-4-2026). Existing confirmed privacy requirements and the plugin's release gates remain in force. A browser rendering of a Plate must clearly state any differences from the in-game renderer.

## Implementation

The server embeds the HTML, CSS and JavaScript in its assembly. There is no frontend package manager, CDN, third party font, analytics script, new runtime container or separate deployment. `/admin/` serves only a static sign-in shell to anonymous visitors. Data and actions require a validated staff session. Normal plugin requests and their signed bodies are unchanged, except that public Plate and image reads honor moderation holds.

GitHub OAuth uses the ASP.NET Core handler, PKCE, a correlation cookie and a fixed callback. The provider token is used to fetch the numeric account ID and is not saved in the session or database. An unapproved account is refused during ticket creation. Staff cookies are Secure, HttpOnly, SameSite=Lax and limited to four hours without sliding renewal. Data protection keys are ephemeral; restarting the server signs everyone out. Staff status and its grant generation are checked on every request and again inside each mutation transaction. Revoking and granting access again does not revive an old session.

All admin requests require HTTPS and the configured deployment hostname. Forwarded HTTPS is accepted only from the existing trusted proxy list. POST requests, however the method is spelled, require an exact same-origin Origin header, JSON, an antiforgery token and a body no larger than 4 KiB; methods other than GET, HEAD and POST are refused with 405. Unknown input fields and out-of-range pages are refused. Per-address limits are 240 requests per minute and 20 login starts per ten minutes, using the existing bounded rate limiter. Responses are no-store and have a restrictive Content Security Policy. The desk sends no HSTS header, because HSTS would apply to the whole host, including the plugin's `/v1` endpoints; staff cookies use the `__Host-` prefix and Secure, and Caddy redirects HTTP to HTTPS. Queries use POST so player identifiers and review reasons do not enter URLs. Request logging retains only the existing route/status metadata. OAuth framework logging is disabled to avoid provider payloads or callback details entering logs.

Only the configured owner can grant or revoke moderators, using positive numeric GitHub IDs. IDs and roles are returned to the UI as strings. The session response carries only the signed-in account's own ID and role, never the owner's ID. Signing out deletes the browser's cookie; a copied cookie stays valid until it expires, the account's access changes or the server restarts, and the owner's session can only be ended by a restart. At most 100 moderator records, including revoked records, are retained. Staff records are access configuration and are not part of the 30-day audit purge. The owner is not removable through the dashboard. This first version has no role editor, bulk actions, ban system, report notifications or activity tracking.

Each hide/restore operation compares the current revision marker and current hold state with what the moderator reviewed. An intervening publication is refused with 409. Every change and its audit entry are committed together. Holds attach to the existing binding, survive republishing or paused sharing and apply even when the dashboard is disabled. A removed binding cascades its hold away and clears the audit target. A new binding does not inherit a ban. Existing downloaded/cached content cannot be recalled. Restoring a hold does not override ordinary allowlist, opt-in or stale-binding rules.

Reports receive random review tokens on migration and insertion. A stale dismiss cannot delete a later report that reused a SQLite row ID. The migration also repairs blank tokens written by an older binary. Dismissing deletes the report and records the action and staff reason, without copying its content or reporter identity. Audit entries are indexed by time and by target Plate, and expire after 30 days, using existing hourly housekeeping and secure-delete/checkpoint behavior; existing backup retention still applies. Staff should never paste personal information or reported content into audit reasons.

The overview uses stored published counts, report counts, held counts and existing health signals. Published includes held content that remains stored. It does not mean concurrent users. Lists show 50 rows at a time and update only when requested. Player text (Plate names, text elements) and report and audit reasons show bidi controls, zero-width characters and the BOM as `[U+XXXX]`, so such text cannot hide or visually reverse itself for a reviewer; Hangul fillers and tag characters are not marked, and character names and worlds come from the Lodestone and are shown as given. The browser preview is explicitly approximate: full text and uploaded images are separately available for review; game fonts, bundled artwork, texture effects, fitting and some layout effects are not reproduced.

## Validation and remaining acceptance

The `AdminDashboardTests` cover default-off routes, host/HTTPS rejection, anonymous access, CSP, OAuth state/PKCE, real handler token exchange against a synthetic provider, approved and unapproved accounts, secure session properties, CSRF/origin/body limits including lowercase method spellings and refused methods, moderator authority, revocation and regrant, expired/tampered sessions, logout, stale revision rejection, hidden lookup/image behavior, republishing, restoring, ownership preservation, stale report IDs, audit expiry, opt-out cascades, migration repair and login rate limits. Test-issued cookies exist only in the test assembly; production has no development login.

Independent security/privacy and correctness/persistence reviews, a browser acceptance run against the real server on HTTPS with a synthetic GitHub stand-in, and exact-head CI are recorded in PR #133, with screenshots under the project's `community-desk-133` folder. That run covered desktop, 390 px and 320 px layouts, both themes, keyboard navigation and focus, reduced motion, confirmation dialogs, empty and error states, session expiry, revocation during an open session, hostile text displayed as text, and the full hide, republish-while-held, stale restore, restore and grant flow with public lookup and image checks.

Still outstanding after a separate, owner-approved deployment:

1. Live HTTPS OAuth with a real GitHub OAuth App: owner login, unapproved account refusal, moderator login and revocation.
2. Live proxy HTTPS and cookie behavior through Caddy, public lookup/image suppression on a real held Plate, preserved ownership and opt-out deletion.
3. Network interruption and rate-limit recovery in a real browser session.

## Sources

- [GitHub OAuth authorization](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie)
- [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery)
