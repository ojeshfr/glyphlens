# Updates: release steps

GlyphLens checks a signed update feed (see "Updates" in [SECURITY.md](SECURITY.md)). The repository ships **without** a feed URL or signing
key, so the updater is inert until a release owner configures both. Nothing below has been run against a real server.

## One-time setup

1. Make a key pair (any machine you control):
   ```powershell
   dotnet run tools/UpdateFeed.cs -- keygen C:\secure\glyphlens-update-private.pem C:\secure\glyphlens-update-public.pem
   ```
   Keep the **private** key out of Git (password manager or offline disk). Anyone holding it can publish updates your users will trust.
2. Put the **public** key and the feed's HTTPS address into `src/GlyphLens.App/Updates/update-config.json`:
   ```json
   { "feedUrl": "https://example.com/glyphlens/stable.json", "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n" }
   ```
   The public key is not secret. Rebuild; the Settings page then shows the Updates section as configured.

## For every release

1. Build the installer and give it a version-stamped name ending in `.exe` or `.msi`, for example `GlyphLens-0.2.0-setup.exe`. Upload it to an
   HTTPS address.
2. Write the release notes (plain text) to a file and sign the feed:
   ```powershell
   dotnet run tools/UpdateFeed.cs -- sign C:\secure\glyphlens-update-private.pem GlyphLens-0.2.0-setup.exe https://example.com/glyphlens/GlyphLens-0.2.0-setup.exe 0.2.0 notes.txt stable.json
   ```
3. Upload `stable.json` to the `feedUrl`. Installed copies see it within about a day (or at once with "Check now").

## Behaviour to know

- Versions are compared numerically (`0.10.0` is newer than `0.9.0`). A feed with the same or a lower version is "up to date".
- The user always chooses to download, and again to install; the installer is verified before and right before it runs.
- The installer is not Authenticode-signed by this process; sign it separately to avoid SmartScreen warnings.
- To stop all update traffic for a user: Settings > Updates > turn off "Check for updates automatically".
