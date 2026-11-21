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
  version: "0.1.0-preview",
  fileName: "GlyphLens-0.1.0-preview-win-x64.zip",
  url: "https://github.com/ojeshfr/glyphlens/releases/download/v0.1.0-preview/GlyphLens-0.1.0-preview-win-x64.zip",
  sizeBytes: 7180510,
  sha256: "dcbe20416b26e61ca73f9105a14751fb313a8a8953afa339f31c41a1b3317124",
  publishedOn: "2026-11-21",
  notesUrl: "https://github.com/ojeshfr/glyphlens/releases/tag/v0.1.0-preview",
  notes: "A developer preview: a zip with the app and the worker source. There is no installer and no model; you set up .NET, Python and the models once.",
};
