# GitHub Operations and Deployment Runbook

## 1. Canonical Repository

- Owner: `SomaliSkinnyAI`
- Repository: `missile-command-overdrive`
- URL: `https://github.com/SomaliSkinnyAI/missile-command-overdrive`
- Primary branch: `main`
- Public Pages URL: `https://somaliskinnyai.github.io/missile-command-overdrive/`

## 2. Repository Layout

- `misslecommand_enhanced.html`: main single-file game runtime
- `index.html`: GitHub Pages launcher/redirect to main game file
- `README.md`: top-level project quickstart
- `docs/`: product + technical + operations documentation

## 3. What Was Done (Historical Record)

Date context: February 22, 2026.

1. Initialized git in local workspace.
2. Added game + readme and made initial commit.
3. Created private GitHub repo and pushed `main`.
4. Added `index.html` entry point for Pages compatibility.
5. Attempted Pages on private repo; blocked by plan.
6. Changed repo visibility to public.
7. Enabled Pages deployment from `main` branch, root path (`/`).
8. Verified Pages build reached `built` status and URL returned HTTP 200.

## 4. Branching and Change Management (recommended)

- Branch model:
  - `main`: production-ready branch
  - `feature/<topic>`: implementation branches
  - `hotfix/<topic>`: urgent production fixes
- PR policy:
  - open PR for all non-trivial changes
  - require at least one review when team size > 1
- Commit style:
  - concise imperative subject (e.g., `Improve raider renderer fidelity`)

## 5. Release Management (recommended)

- Tag stable milestones using semantic versioning (`v0.1.0`, `v0.2.0`)
- Maintain release notes including:
  - gameplay changes
  - balancing changes
  - technical fixes
  - known issues

## 6. GitHub Pages Operations

### Enable/Update Source

- Repo Settings -> Pages
- Source: `Deploy from a branch`
- Branch: `main`
- Folder: `/ (root)`

### Verification

- Check Pages status in Settings -> Pages
- Optional CLI:
  - `gh api repos/<owner>/<repo>/pages`

## 7. Auth and Access Operations

### Initial auth

- `gh auth login`
- Confirm with `gh auth status`

### Permissions model

- Public repo does **not** grant write access to everyone.
- Only owner/collaborators can push directly.
- External contributors use forks/PRs.

## 8. Backup and Recovery

- Always keep local clone synced with `origin/main`.
- For rollback:
  - identify prior commit/tag
  - revert with explicit commit
  - push and verify Pages rebuild

## 9. Security and Governance

- Do not commit secrets/tokens.
- Prefer GitHub-provided auth flows (`gh auth login`) over raw token embedding.
- If opening collaboration, enable branch protection on `main`.
