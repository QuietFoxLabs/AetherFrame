# AetherFrame Community Desk

Rebuilt October 4, 2026, at the owner's request, on `codex/admin-dashboard-rebuild`. Implementation is saved in small GitHub checkpoints and remains disabled by default. Independent reviews, CI and live browser acceptance are still required. Nothing is deployed or activated by this PR.

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

## Implementation

The server embeds the HTML, CSS and JavaScript in its assembly. There is no frontend package manager, CDN, third party font, analytics script, new runtime container or separate deployment. `/admin/` serves only a static sign-in shell to anonymous visitors. Data and actions require a validated staff session. Normal plugin requests and their signed bodies are unchanged, except that public Plate and image reads honor moderation holds.

GitHub OAuth uses the ASP.NET Core handler, PKCE, a correlation cookie and a fixed callback. The provider token is used to fetch the numeric account ID and is not saved in the session or database. An unapproved account is refused during ticket creation. Staff cookies are Secure, HttpOnly, SameSite=Lax and limited to four hours without sliding renewal. Data protection keys are ephemeral; restarting the server signs everyone out. Staff status and its grant generation are checked on every request and again inside each mutation transaction. Revoking and granting access again does not revive an old session.

All admin requests require HTTPS and the configured deployment hostname. Forwarded HTTPS is accepted only from the existing trusted proxy list. POST requests require an exact same-origin Origin header, JSON, an antiforgery token and a body no larger than 4 KiB. Unknown input fields and out-of-range pages are refused. Per-address limits are 240 requests per minute and 20 login starts per ten minutes, using the existing bounded rate limiter. Responses are no-store and have a restrictive Content Security Policy. Queries use POST so player identifiers and review reasons do not enter URLs. Request logging retains only the existing route/status metadata. OAuth framework logging is disabled to avoid provider payloads or callback details entering logs.

Only the configured owner can grant or revoke moderators, using positive numeric GitHub IDs. IDs and roles are returned to the UI as strings. At most 100 moderator records, including revoked records, are retained. Staff records are access configuration and are not part of the 30-day audit purge. The owner is not removable through the dashboard. This first version has no role editor, bulk actions, ban system, report notifications or activity tracking.

Each hide/restore operation compares the current revision marker and current hold state with what the moderator reviewed. An intervening publication is refused with 409. Every change and its audit entry are committed together. Holds attach to the existing binding, survive republishing or paused sharing and apply even when the dashboard is disabled. A removed binding cascades its hold away and clears the audit target. A new binding does not inherit a ban. Existing downloaded/cached content cannot be recalled. Restoring a hold does not override ordinary allowlist, opt-in or stale-binding rules.

Reports receive random review tokens on migration and insertion. A stale dismiss cannot delete a later report that reused a SQLite row ID. The migration also repairs blank tokens written by an older binary. Dismissing deletes the report and records the action and staff reason, without copying its content or reporter identity. Audit entries expire after 30 days, using existing hourly housekeeping and secure-delete/checkpoint behavior; existing backup retention still applies. Staff should never paste personal information or reported content into audit reasons.

The overview uses stored published counts, report counts, held counts and existing health signals. Published includes held content that remains stored. It does not mean concurrent users. Lists show 50 rows at a time and update only when requested. The browser preview is explicitly approximate: full text and uploaded images are separately available for review; game fonts, bundled artwork, texture effects, fitting and some layout effects are not reproduced.

## Validation and remaining acceptance

The new `AdminDashboardTests` cover default-off routes, host/HTTPS rejection, anonymous access, CSP, OAuth state/PKCE, real handler token exchange against a synthetic provider, approved and unapproved accounts, secure session properties, CSRF/origin/body limits, moderator authority, revocation and regrant, expired/tampered sessions, logout, stale revision rejection, hidden lookup/image behavior, republishing, restoring, ownership preservation, stale report IDs, audit expiry, opt-out cascades, migration repair and login rate limits. Test-issued cookies exist only in the test assembly; production has no development login.

The local .NET 10 Release solution build passed with zero warnings and errors. All 15 new dashboard cases pass. Plugin (5,290), protocol (432), persona (472) and release tooling (474) suites pass. The complete local server suite is not green: this workspace denies Unix-domain sockets, and an existing pipe test also failed once during the combined run. The unchanged base `488c768` reproduces the same 15 worker/socket-related failures (275 pass, 15 fail). The combined dashboard run has 289 pass and 16 fail, adding one transient `AMessageBeforeOpen_EndsTheSession` failure. A focused rerun excluding the two socket-dependent test classes passes all 251 cases, including that pipe test. Exact-head CI is still required. Do not treat a filtered test run as full validation.

The cloud browser refused local-file previews. Actual desktop/mobile rendering, keyboard/focus behavior, theme contrast and the live HTTPS OAuth round trip are not yet verified. Before readiness, independently review security and correctness at an explicit SHA and run:

1. Desktop and 390 px/320 px layouts, both themes, keyboard-only navigation and reduced motion.
2. Approved owner login, unapproved account rejection, moderator login and revocation during an open session.
3. Empty and populated views; full text and image review; deliberate HTML-like content displayed as plain text.
4. Cancel and confirm every moderation action; stale content and report conflicts; no accidental repeat action.
5. Logout/expired session, network interruption, unavailable image and rate-limit recovery.
6. Live proxy HTTPS and cookie behavior, current public lookup/image suppression, preserved ownership and opt-out deletion.

## Sources

- [GitHub OAuth authorization](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie)
- [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery)
