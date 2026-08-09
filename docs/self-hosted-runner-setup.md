# Self-hosted Runner Setup

Use these steps to attach a dedicated Windows runner to the new backend repository.

## Recommended configuration

- Repository: `ayahmed_mhc/QAF-OnPrem--backend-dotnet`
- Runner name: `tf-backend-dotnet-win-01`
- Runner folder: `C:\actions-runner-backend-dotnet`
- Labels: default labels only (`self-hosted`, `Windows`, `X64`)

## Preparation

1. In GitHub, open the repository settings for Actions runners
2. Choose `New self-hosted runner`
3. Select `Windows`
4. Keep the registration page open because the token expires quickly

## Local setup

1. Create the folder `C:\actions-runner-backend-dotnet`
2. Extract the GitHub Actions runner package into that folder
3. From that folder, run the `config.cmd` command shown by GitHub
4. Start the runner with `run.cmd`

## Verification

1. In GitHub, confirm the runner shows as `Idle`
2. Push a small commit to `development` or open a PR into `testing`
3. Confirm the `Backend CI / build-test` workflow starts on the self-hosted runner
4. After that, add the required status check in branch protection

## Notes

- Do not reuse the old backend runner because it is attached to a different repository
- Keep the old backend runner intact to avoid breaking its existing automation