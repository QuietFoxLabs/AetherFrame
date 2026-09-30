# AetherFrame server runbook

How the owner sets up and runs the AetherFrame server for the two-player test (NETWORK2.md, section 2). Everything here that needs an account, a payment, a key or a secret is the owner's. Claude prepares the kit and never sees a credential.

The kit is in [`deploy/`](../../deploy):
- two container images, the server and the image worker;
- Caddy for HTTPS;
- `compose.yaml`, which runs Caddy and the server;
- `aetherframe-worker.sh`, which runs the image worker one container at a time as a system service;
- `host-setup.sh`, for a fresh server;
- the **Deploy the server** workflow.

## 1. What to buy

- **A domain**, kept on automatic renewal. A lapsed domain could be registered by someone else, who could then answer players' plugins (decision R2). Any registrar works. The server will answer on one hostname under it, for example `plates.yourdomain.net`.
- **A small Linux server** (a VPS): Ubuntu 24.04, 2 GB of memory, 1 or 2 CPUs, about 20 GB of disk, and a public IPv4 address. A few dollars a month from any VPS provider is enough for two testers.

## 2. Setting up the server

1. **Point the hostname at the server.** At the registrar or DNS host, add an `A` record for the hostname with the server's IPv4 address.
   - Don't add an `AAAA` (IPv6) record for now. Docker would pass IPv6 visitors on from one internal address, so they would all share one visitor's rate limits (decision R4).
   - IPv6 comes with a later change to the kit.
2. **Make the deploy key**, on your own PC, in PowerShell or a terminal:
   ```
   ssh-keygen -t ed25519 -f aetherframe-deploy -N "" -C aetherframe-deploy
   ```
   This makes two files: `aetherframe-deploy` (private: it goes only into GitHub, in step 4) and `aetherframe-deploy.pub` (public).
3. **Run the setup script** on the server, signed in as root or a sudo user, with the public key's line as its argument:
   ```
   curl -fsSLO https://raw.githubusercontent.com/QuietFoxLabs/AetherFrame/master/deploy/host-setup.sh
   sudo bash host-setup.sh "$(cat aetherframe-deploy.pub)"
   ```
   (Copy `aetherframe-deploy.pub` to the server first, or paste its one line in place of `$(cat ...)`.) The script:
   - installs Docker;
   - keeps logs 14 days (decision S5);
   - allows only SSH, HTTP and HTTPS through the firewall;
   - turns on automatic security updates;
   - creates the `aetherframe-deploy` user;
   - makes `/opt/aetherframe/config/aetherframe.json`;
   - installs the `aetherframe-worker` service, which starts working after the first deploy.
4. **Create the GitHub environment.** In the repository's **Settings → Environments**, create `production`:
   - **Required reviewers:** yourself. Every deploy then waits for your approval.
   - **Deployment branches:** `master` only.
   - **Secrets:**
     - `DEPLOY_HOST`: the server's hostname or IP address;
     - `DEPLOY_SSH_KEY`: the whole contents of the private file `aetherframe-deploy`;
     - `DEPLOY_KNOWN_HOSTS`: the output of `ssh-keyscan -t ed25519 <DEPLOY_HOST>`, run from your PC with exactly the value you put in `DEPLOY_HOST`. Before you save it, check that its key matches the one your provider's console shows for the server (many show it on first boot). A mismatch means you reached something else.
   - **Variables:** `DEPLOY_DOMAIN` is the hostname from step 1, for example `plates.yourdomain.net`.
5. **Deploy.** In **Actions → Deploy the server → Run workflow**, give the full SHA of the commit on master to deploy. Claude names it in the Owner inbox. Approve the run when GitHub asks.
   - The workflow builds both images, copies them and the compose files to the server, and starts them.
   - It then checks that `https://<hostname>/v1/status` answers.
   - Caddy gets the certificate by itself on the first start.
6. **Add the testers.** Each tester's Lodestone id is the number in their character's Lodestone address, `https://na.finalfantasyxiv.com/lodestone/character/<id>/`. Put both ids in `/opt/aetherframe/config/aetherframe.json` on the server:
   ```
   { "AetherFrame": { "AllowedLodestoneIds": [ "12345678", "23456789" ] } }
   ```
   The server reads the file again within a few seconds. Nothing else is needed. Taking an id out stops that character from checking, publishing and viewing at once (decision C8).

## 3. Running it

Commands are run on the server as `aetherframe-deploy`, in `/opt/aetherframe`.

| To | Run |
| --- | --- |
| See that it's up | `docker compose ps`, and open `https://<hostname>/v1/status` |
| See the image worker's runs | `systemctl status aetherframe-worker` and `docker ps --filter name=aetherframe-worker` |
| Read the logs (14 days, no addresses or names) | `docker compose logs --since 1h` |
| List bound characters | `docker compose exec server dotnet AetherFrame.Server.dll admin characters` |
| List reports | `docker compose exec server dotnet AetherFrame.Server.dll admin reports` |
| Close a report you've dealt with | `docker compose exec server dotnet AetherFrame.Server.dll admin resolve-report <number>` |
| Remove a character and everything it published | `docker compose exec server dotnet AetherFrame.Server.dll admin remove-character <Lodestone id>` |
| See the configured allowlist | `docker compose exec server dotnet AetherFrame.Server.dll admin allowlist` |
| Restart the image worker | `sudo systemctl restart aetherframe-worker` (a deploy does this itself) |
| Update to a newer commit | run **Deploy the server** again with that commit |
| Stop everything | `docker compose down` (the data stays in its volumes) |

- **The allowlist file.** After you edit it, run `admin allowlist` to see the ids the server now has. A file the server can't read is ignored on a reload: the last good list stays, and the log says so. At a restart, a file it can't read stops the server until it is fixed. An entry that isn't a Lodestone id is ignored, and `admin allowlist` marks it.
- **While the server is stopped**, its daily backup and its clean-up don't run, so older copies aren't deleted. Stop it only briefly, or delete old copies by hand (section 4).

- **Reports** are kept for 30 days, or until you close them (decision C5). To act on one, look at the reported character's Plate in game. If it has to go, remove the character, and take its id off the allowlist if needed.
- **Removal on request** (decision S3). A tester who asks to be removed, and whom you've confirmed out of band (in person, or in game), is removed with `remove-character`. Removing deletes at once exactly what opting out deletes.
- **A stolen or lost key.** The tester checks their character again from AetherFrame on the new or cleaned PC. That moves the character to the new key and deletes everything the old key published (decision C1). If the old PC is compromised, remove the character first, then have them check again.
- **The Lodestone changes its page layout.** Checks and re-reads fail closed: no binding is lost, because only the Lodestone's own "not found" page, seen twice a day apart, removes one. Tell Claude in the Owner inbox; the parser is updated and redeployed.
- **The Lodestone check's ownership rule** rests on the character profile being editable only by its signed-in owner (decision C2). If Square Enix ever changes that, turn sharing off by emptying the allowlist, and tell Claude.

## 4. Backups and what is kept

- **Backups.** The server writes a copy of its database to the `backups` volume once a day and deletes each copy after **7 days**. A Plate a player deletes, by opting out or pausing, is therefore gone from every copy within 7 days (decision D1). The consent text says so.
  - To keep a copy off the server, list the copies and copy one out, in `/opt/aetherframe`:
    ```
    docker compose exec server ls /backups
    docker compose cp server:/backups/server-YYYYMMDD.db ./server-YYYYMMDD.db
    ```
    Then download it with `scp` or your provider's file tools. It holds what testers published, so keep it private, and delete it, both there and on the server, within the same 7 days.
- **Restoring a backup**, in `/opt/aetherframe`, with the copy in that folder:
  ```
  docker compose stop server
  docker compose cp ./server-YYYYMMDD.db server:/data/server.db
  docker compose run --rm --no-deps --entrypoint rm server -f /data/server.db-wal /data/server.db-shm
  docker compose start server
  ```
  A restore brings back anything deleted since the backup was made. Only restore one made after the last removal, or remove again afterwards.
- **What the server keeps** (decision C7): for each bound character, the Lodestone id, name and World, the key's identity and the profile id, the latest Plate and its images, and its revision records; and reports. It keeps no addresses, no lookup log and no copy of a Lodestone page. Logs hold only a request's route, status, time and kind of failure, for 14 days.

## 5. What each part does

- **Caddy** answers HTTPS on ports 443 and 80 and passes requests to the server. It keeps no access log, and its error logs, which could hold an address, are switched off.
- **The server** listens only inside Docker. It trusts the client address Caddy reports and no other, and refuses to start with a name reserved for tests or ASP.NET Core's forwarded-headers switch on.
- **The image worker** runs from the `aetherframe-worker` service, one container at a time (decision I2).
  - Each container takes at most one image, then ends; an idle one ends after 15 seconds. Whatever it is doing, it is ended from outside after 60 seconds, and a fresh one starts. The service removes any worker container left behind when it starts or stops.
  - It has no network, a read-only file system, 512 MB of memory, and 64 processes at most.
  - It runs as its own user in the server's group, so it can reach the server's socket and nothing else.
  - It refuses to run if it finds a network.
  - If it keeps failing, publishes with images get "try again later", and publishes without images still work.
