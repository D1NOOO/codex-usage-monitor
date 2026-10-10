# Project collaboration rules

## Git commits and pushes

- Write commit messages and submission summaries in the normal concise style appropriate to the change.
- Ordinary code commits and pushes follow the user's authorized task scope.

## Documentation encoding

- Read and write documentation explicitly as UTF-8; do not rely on the shell's default encoding.
- Follow `.editorconfig`: Markdown uses UTF-8 without BOM and LF line endings.
- Run `scripts/verify.ps1` after documentation edits. It checks public documentation for invalid UTF-8 and known mojibake patterns.

## New version releases

- Release changelogs must include Chinese first, followed by English.
- Both language sections should convey the same main changes.
- Keep the notes concise and focused on meaningful changes; avoid an exhaustive list of implementation details.
- Put the most important changes first in each language section.
- Group entries under `修复` and `改进` headings, followed by matching English `Fixes` and `Improvements` sections. Use bulleted entries beneath the headings and Markdown hyperlinks for relevant issues.
- Send the proposed changelog to the user for review before submitting a version tag.
- Wait for the user to confirm that they have reviewed the notes and approve proceeding before creating or pushing the version tag or triggering a release through another route. Do not infer approval from silence or permission to commit ordinary code.
