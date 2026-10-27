# Contributing

## Branches

Work happens on short-lived branches cut from `main` and merged back through a pull request.
Name branches `<type>/<short-description>`, in lowercase with hyphens:

```
feat/windows-installer
fix/voice-rejoin-after-reconnect
docs/vps-setup
```

`<type>` is one of the commit types below. `feature/`, `bugfix/`, `hotfix/` and `release/` are
accepted as well, and `dependabot/` branches are left alone.

## Commit messages

Commits follow [Conventional Commits 1.0](https://www.conventionalcommits.org/en/v1.0.0/):

```
<type>[(<scope>)][!]: <description>

[body]

[footer(s)]
```

| Type | Use for |
| --- | --- |
| `feat` | A new feature |
| `fix` | A bug fix |
| `perf` | A performance improvement |
| `refactor` | A code change that neither fixes a bug nor adds a feature |
| `test` | Adding or correcting tests |
| `docs` | Documentation only |
| `style` | Formatting, whitespace and the like |
| `build` | The build system, project files or dependencies |
| `ci` | GitHub Actions workflows |
| `chore` | Anything else that does not touch the shipped code |
| `revert` | Reverting an earlier commit |

Common scopes are the area of the code: `server`, `client`, `media`, `voice`, `stream`, `chat`,
`installer`, `release`. Mark breaking changes with `!` after the type or scope, or with a
`BREAKING CHANGE:` footer; that includes protocol changes that require everyone to update the client.

Examples:

```
feat(voice): add push-to-talk
fix(server): reject voice packets larger than an Opus frame
ci(release): publish the Windows installer
feat(server)!: require clients to send their version when connecting
```

Write the description in the imperative mood ("add", not "added"), without a capital letter or a
final full stop, and keep the subject line under about 72 characters.

## Pull requests

- Give the pull request a Conventional Commits title: it becomes the commit message when the pull
  request is squash-merged, which is the preferred way to merge.
- The *Conventions* workflow checks the branch name, the pull request title and every commit message
  (merge commits excepted).
- CI must pass: `dotnet build` and `dotnet test` (see the README for local requirements).

## Releases

Releases are cut from `main` by the *Release* workflow (see the README). Version numbers follow
[Semantic Versioning](https://semver.org/): `feat` changes bump the minor version, `fix` changes the
patch version, and breaking changes the major version (the minor version while still on `0.x`).
