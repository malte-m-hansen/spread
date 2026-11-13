# Releasing Spread

Releases are built by GitHub Actions (`.github/workflows/release.yml`) so that every download comes from a clean machine and the same steps.

## Make a release

1. Pick the version and set `<Version>` in `Spread.csproj` (for example `0.2.0`). Commit it to `main`.
2. Tag it and push the tag:
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
   The tag must be `v` plus the csproj version; the workflow stops with an error if they differ.
3. The workflow builds, runs the self-tests, zips `Spread-v0.2.0-win-x64.zip` with `Spread.cmd`, writes a `.sha256` file, and creates the release with generated notes. A tag with a dash (`v0.2.0-beta.1`) is marked as a pre-release.
4. Open the release page and read the notes. Edit them if you want.

## Try it without releasing

On the Actions tab choose "release" > "Run workflow". It does the same build and tests but keeps the zip only as a downloadable artifact of that run; no tag and no release are created.

## If a release is wrong

Delete the release and the tag (`gh release delete v0.2.0 --cleanup-tag`), fix the problem, and tag again. Do not reuse a version number people may already have downloaded: make a new one.

## Code signing (not done yet)

The zip contains no .exe, so there is nothing for virus scanners to flag. If an .exe is wanted later, it should be signed (SignPath Foundation for open source, Azure Trusted Signing, or a normal certificate) and the signing step added to the workflow before packaging.
