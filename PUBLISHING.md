# Publishing checklist

Use this checklist before making the repository public.

## Identity privacy

- Decide whether the repository should live under a personal account or a
  separate organization.
- Configure a GitHub no-reply commit email before the first commit:

  ```powershell
  git config user.name "YOUR_PUBLIC_DISPLAY_NAME"
  git config user.email "YOUR_GITHUB_NOREPLY_ADDRESS"
  ```

- In GitHub email settings, enable **Keep my email addresses private** and
  **Block command line pushes that expose my email**.
- Review the author/committer metadata of every commit before pushing:

  ```powershell
  git log --format=fuller
  ```

## Repository setup

- Confirm the repository URLs in all three README files match the public repository.
- Keep the default branch named `main`.
- Enable branch protection/rules for `main`.
- Require the CI workflow before merging pull requests.
- Enable private vulnerability reporting.
- Confirm Secret scanning and Push protection are enabled/available.
- Keep Actions' default token permission read-only; the Release job grants its
  own minimal write permissions.

## Never publish

- `auth.json` or any copied content from it;
- access/refresh tokens, cookies, API keys, or authorization headers;
- personal runtime `settings.json`;
- application logs or terminal transcripts containing private paths;
- screenshots showing account names, avatars, email, project names, chats,
  desktop files, or other personal content;
- `.git` history imported from an earlier private workspace without auditing
  every commit.

Deleting a secret in a later commit is not enough: Git history preserves it.
If a real secret is committed, revoke/rotate it immediately and rewrite the
history before publishing.

## Release checklist

- Update `version.txt`.
- Run:

  ```powershell
  .\scripts\verify.ps1
  .\scripts\build.ps1 -Package
  ```

- Test Simplified Chinese, Traditional Chinese, and English.
- Test at 100% and 125% Windows display scaling.
- Review the packaged ZIP and `SHA256SUMS.txt`.
- Create and push a matching `vMAJOR.MINOR.PATCH` tag.
- Confirm the GitHub Release contains only:
  - the Windows ZIP;
  - `SHA256SUMS.txt`;
  - generated release notes.
- Verify the GitHub artifact attestation.

## Known trust limitation

The project does not currently code-sign the Windows executable. SmartScreen
may show an unknown-publisher warning. GitHub build provenance and SHA256
checksums improve traceability but are not a replacement for Authenticode
code signing. Consider signing future releases with a protected code-signing
certificate or managed signing service.
