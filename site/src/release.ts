/**
 * The single source of truth for the Download section.
 *
 * While `published` is false the page says honestly that no build exists yet and links to the Releases page.
 * Now that a build is published, set `published: true` and fill in every field from the real file:
 *   - sizeBytes: (Get-Item <file>).Length
 *   - sha256:    (Get-FileHash -Algorithm SHA256 <file>).Hash.ToLower()
 * Never fill these with placeholders: the page shows them to people who will verify their download against them.
 */
export type Release =
  | { published: false }
  | {
      published: true;
      /** Version as shown in the app, for example "0.2.0". */
      version: string;
      /** File name of the download, for example "GlyphLens-0.1.0-preview-win-x64.zip". */
      fileName: string;
      /** HTTPS address of the file (normally an asset of a GitHub release). */
      url: string;
      /** Exact size in bytes. */
      sizeBytes: number;
      /** Lower-case hexadecimal SHA-256 of the file (64 characters). */
      sha256: string;
      /** Publication date, YYYY-MM-DD. */
      publishedOn: string;
      /** Address of the release notes (normally the GitHub release page). */
      notesUrl: string;
      /** One or two sentences about this build. */
      notes: string;
    };

export const RELEASES_PAGE = "https://github.com/ojeshfr/glyphlens/releases";
export const REPOSITORY = "https://github.com/ojeshfr/glyphlens";

export const release: Release = {
  published: true,
  version: "0.1.1-preview",
  fileName: "GlyphLens-0.1.1-preview-win-x64.zip",
  url: "https://github.com/ojeshfr/glyphlens/releases/download/v0.1.1-preview/GlyphLens-0.1.1-preview-win-x64.zip",
  sizeBytes: 70478111,
  sha256: "e7a9c1b3e8bf8a0e09b6a60b7fdcf87d391b8fd3724f7999cf5ea8b77ccd9e68",
  publishedOn: "2026-11-21",
  notesUrl: "https://github.com/ojeshfr/glyphlens/releases/tag/v0.1.1-preview",
  notes: "A developer preview: a self-contained zip, no installer. On the first run, one button installs Python, the engines and the Chinese models (about 360 MB).",
};
