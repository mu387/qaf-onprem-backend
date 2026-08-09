# Backend Branch Protection Guide

Apply these GitHub branch protection rules to keep backend promotion aligned with `development -> testing -> main`.

## Branches

- `development`
- `testing`
- `main`

## Recommended rules for `testing`

- Require a pull request before merging
- Require at least 1 approval
- Dismiss stale approvals when new commits are pushed
- Require status checks to pass before merging
- Select the `Backend CI / build-test` status check
- Require branches to be up to date before merging
- Restrict direct pushes

## Recommended rules for `main`

- Require a pull request before merging
- Require at least 1 approval
- Dismiss stale approvals when new commits are pushed
- Require status checks to pass before merging
- Select the `Backend CI / build-test` status check
- Require branches to be up to date before merging
- Restrict direct pushes
- Consider requiring conversation resolution before merging

## Optional rules for `development`

- Require a pull request before merging if you want stricter control
- Otherwise allow maintainers to merge directly for fast iteration
- Keep the `Backend CI / build-test` check enabled on pull requests

## Promotion flow

1. Create a feature branch from `development`
2. Open a pull request into `development`
3. After validation, promote `development` into `testing`
4. After QA signoff, promote `testing` into `main`

## Notes

- The current workflow uses a self-hosted Windows runner with labels `self-hosted`, `Windows`, and `X64`
- This is suitable for source validation: restore, build, and test
- Register the runner for the new backend repository before requiring the `Backend CI / build-test` status check

## Self-hosted runner

- Recommended runner name: `tf-backend-dotnet-win-01`
- Recommended folder: `C:\actions-runner-backend-dotnet`
- Register it against the repository `ayahmed_mhc/QAF-OnPrem--backend-dotnet`