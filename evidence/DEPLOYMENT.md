# Deployment (GitHub Pages)

- Target: GitHub Pages, workflow `.github/workflows/deploy.yml` (build → deploy-pages → deployed-acceptance). The owner enabled Pages (source: GitHub Actions) on 2026-11-01.
- The gate `vars.DEPLOY_TARGET == 'github-pages'` was never true in Actions (every run skipped through 19:34Z), so PR #24 gates on the repository instead (owner confirmed Pages as the target).
- First deployment: run https://github.com/kemiller2002/indy-init-rehearsal-008/actions/runs/41342757831 at main `b45e969e79673544d4a3383416dd724356de8dbc`. build 19:38:35–19:39:57Z (site assembled, stack-lock gate passed, Pages artifact uploaded); deploy 19:40:03–19:40:10Z; deployed-acceptance 19:40:15–19:40:16Z: "The deployed site serves this exact SHA and its engine" passed. That is T+129 min.
- A dispatch from a branch (run 41342464933) built successfully but deploy failed before starting: the github-pages environment only accepts main. Expected.
- Every later push to main redeploys and re-verifies the deployed SHA. The final verdict names the last verified SHA.
