# Project maintenance

- The user requests that future updates also be synchronized to `3tea-1heart/qzone-timecapsule`. After requested changes pass relevant checks, commit and push them. Do not overwrite unrelated work or force-push.
- Never commit QQ exports, real fixture account identifiers, login profiles, cookies, credentials, logs, or private screenshots. Stage explicit source/documentation paths and inspect staged changes before pushing.
- Tests use synthetic fixtures. Use a fresh output directory per regression run. Distinguish synthetic checks, offline samples and real-account completeness.
- For a release, update the project version and Chinese changelog, test and build, then push main and a matching vX.Y.Z tag. Verify Actions and release assets before claiming delivery.
- Do not add or change the project license without the owner's choice; preserve third-party notices.
