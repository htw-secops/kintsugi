namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update/--install/--uninstall script for
/// everything Homebrew manages, Homebrew itself included. Shared between the "Find Upgrade Paths" research flow
/// (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both only ever
/// produce this one script shape under the <see cref="PlatformBucket.ForPackageManager"/> bucket the
/// scan planner expects for every package-manager-managed row — never AI-generated, since
/// Homebrew's upgrade mechanics are already fully known and there's nothing to research.
/// The package name is never baked into the returned text — both the server (via
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and the macOS agent (via
/// <c>patch_one</c>) already invoke every script with <c>--appName &lt;name&gt;</c> as a real
/// argument, so the script reads it at runtime instead. That means <see cref="Build"/> returns
/// byte-identical content for every formula and cask — a human only ever needs to review and sign
/// one script, and every other row sharing that exact content inherits the same signature
/// automatically (see <c>ResearchApplicationUpgradePathCommandHandler.UpsertAsync</c>,
/// <c>RegisterApplicationsCommandHandler.UpsertPackageManagerUpgradePathsAsync</c>, and
/// <c>SignUpgradePathScriptCommandHandler</c>) rather than needing its own separate review.
/// --update-version runs under plain bash + curl against Homebrew's own public API so it works
/// unattended on this (Linux) server, exactly like the AI-authored contract requires; --update runs
/// the actual `brew` commands, which only ever exist on the managed Mac.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="SnapUpgradeScript"/>, this is one text for Homebrew's own row and every formula
/// it manages (see <see cref="RecognizedPackageManager.BuildScript"/>) — but for the opposite
/// reason. snapd is itself a snap, so one text serves both cases
/// by accident of the platform; Homebrew is <em>not</em> a formula, and its own row used to get a
/// second script for that reason. That second script cost more than it bought: the Applications
/// screen nests every formula and cask under the "Homebrew" row and shows the manager's script
/// there once, on behalf of all of them, and a row whose own bytes differ from its children's is
/// exactly the case that presentation cannot make honest. So the one script tells Homebrew's own
/// row apart at <em>runtime</em>, by <c>--appName</c> — the same name the agent reports it under
/// (<c>system_info::HOMEBREW_NAME</c>) — and answers its version from GitHub's releases redirect
/// rather than the formula API. The rows still share bytes, so one signature covers Homebrew and
/// everything it manages, which is what lets the manager's row stand in for its children.
/// </para>
/// <para>
/// Every --update also upgrades Homebrew itself, deliberately: <c>brew update</c> is Homebrew's own
/// self-update (it pulls brew's repository to its latest release tag and migrates) as well as the
/// index refresh <c>brew upgrade</c> needs to see a version released since the last one. Homebrew's
/// own row runs only that step — the applications each have a row of their own to be upgraded
/// through, once a human has approved them, and a blanket <c>brew upgrade</c> here would patch every
/// formula on the host regardless.
/// </para>
/// </remarks>
public static class HomebrewUpgradeScript
{
    public static string Build()
    {
        return """
            #!/bin/bash
            set -euo pipefail

            usage() {
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update|--install|--uninstall)" >&2
              exit 1
            }

            APP_NAME=""
            APP_ID=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) APP_NAME="$2"; shift 2 ;;
                --appId) APP_ID="$2"; shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
                --install) [ -n "$MODE" ] && usage; MODE="install"; shift ;;
                --uninstall) [ -n "$MODE" ] && usage; MODE="uninstall"; shift ;;
                *) usage ;;
              esac
            done
            [ -n "$APP_NAME" ] || usage
            [ -n "$MODE" ] || usage

            # Homebrew's own row. The agent reports Homebrew itself as an application named "Homebrew"
            # (see system_info::HOMEBREW_NAME in the macOS agent), and it is not a formula: neither
            # URL below knows it, and `brew upgrade homebrew` would be an error. One script serves it
            # and everything it manages so that one review covers all of them; this is where the two
            # part ways.
            is_homebrew_itself() {
              [ "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')" = "homebrew" ]
            }

            # Both URLs are case-sensitive, and $APP_NAME is not always the lowercase token brew
            # knows the package by: PrepareUpgradePathScanQueryHandler groups an application's
            # variants case-insensitively, so a row whose name settled on a display-cased
            # /Applications bundle ("Nextcloud") rather than on `brew list`'s output ("nextcloud")
            # 404s on every URL below and leaves LatestVersion null — which makes updateAvailable
            # false, which makes the agent's is_patchable false, so the application silently never
            # patches. `brew` itself downcases its argument, so only this lookup is affected.
            # Tried as given first: a row already named by its token must not be transformed.
            latest_version() {
              local candidate response version redirect

              if is_homebrew_itself; then
                # -fsS, deliberately NOT -fsSL: %{redirect_url} reports the redirect curl did *not*
                # follow, so adding -L makes curl follow it to the tag page and report an empty
                # string — the check then fails, LatestVersion stays null, and Homebrew's own row
                # silently never updates.
                redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/Homebrew/brew/releases/latest") || return 1
                [ -n "$redirect" ] || return 1
                printf '%s' "${redirect##*/}"
                return 0
              fi

              for candidate in "$APP_NAME" "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')"; do
                response=$(curl -fsSL "https://formulae.brew.sh/api/formula/${candidate}.json" 2>/dev/null) || \
                  response=$(curl -fsSL "https://formulae.brew.sh/api/cask/${candidate}.json" 2>/dev/null) || continue

                version=$(printf '%s' "$response" | grep -o '"stable":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                if [ -z "$version" ]; then
                  version=$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                fi

                [ -n "$version" ] || continue
                printf '%s' "$version"
                return 0
              done

              return 1
            }

            if [ "$MODE" = "update-version" ]; then
              version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
              printf '%s\n' "$version"
              exit 0
            fi

            # --install / --uninstall: software titles. The macOS agent runs every action row from its
            # root daemon, and Homebrew refuses to run as root, so brew is found by path (root's PATH
            # does not have it) and run as the user who owns the Homebrew installation. Idempotent: an
            # install of something already there, or a removal of something absent, changes nothing.
            if [ "$MODE" = "install" ] || [ "$MODE" = "uninstall" ]; then
              [ -n "$APP_ID" ] || usage
              if is_homebrew_itself; then
                echo "Homebrew cannot install or remove itself" >&2
                exit 1
              fi

              brew_bin=""
              for candidate in /opt/homebrew/bin/brew /usr/local/bin/brew "$(command -v brew 2>/dev/null || true)"; do
                if [ -n "$candidate" ] && [ -x "$candidate" ]; then
                  brew_bin="$candidate"
                  break
                fi
              done
              if [ -z "$brew_bin" ]; then
                echo "Homebrew is not installed on this Mac, so $APP_ID cannot be ${MODE}ed through it" >&2
                exit 1
              fi

              # The prefix is the directory above bin/brew, read from the path rather than from
              # `brew --prefix`, which would itself be brew running as root. On an Intel Mac the prefix
              # is /usr/local, which root owns, and the user's installation is /usr/local/Homebrew.
              brew_prefix=$(cd "$(dirname "$brew_bin")/.." && pwd)
              brew_home="$brew_prefix"
              [ -d "$brew_prefix/Homebrew" ] && brew_home="$brew_prefix/Homebrew"
              brew_owner=$(stat -f %Su "$brew_home")
              if [ -z "$brew_owner" ] || [ "$brew_owner" = "root" ]; then
                echo "could not find the user who owns Homebrew at $brew_home; Homebrew will not run as root" >&2
                exit 1
              fi

              as_brew_owner() {
                if [ "$(id -un)" = "$brew_owner" ]; then
                  (cd / && NONINTERACTIVE=1 HOMEBREW_NO_ENV_HINTS=1 "$brew_bin" "$@")
                else
                  (cd / && sudo -u "$brew_owner" -H env NONINTERACTIVE=1 HOMEBREW_NO_ENV_HINTS=1 "$brew_bin" "$@")
                fi
              }

              if as_brew_owner list "$APP_ID" >/dev/null 2>&1; then
                present=yes
              else
                present=no
              fi
              if { [ "$MODE" = "install" ] && [ "$present" = "yes" ]; } || { [ "$MODE" = "uninstall" ] && [ "$present" = "no" ]; }; then
                echo "$APP_ID is already in the wanted state; nothing to do"
                exit 0
              fi

              as_brew_owner "$MODE" "$APP_ID"
              exit 0
            fi

            # --update mode: runs on the managed Mac itself, where `brew` actually exists.
            if ! command -v brew >/dev/null 2>&1; then
              echo "homebrew is not installed" >&2
              exit 1
            fi

            # `brew update` is Homebrew upgrading itself — it pulls brew's own repository to its latest
            # release tag — and is also what refreshes the formula/cask index; without it `brew upgrade`
            # acts on a stale catalog and reports "already up to date" for a version released since the
            # index was last refreshed. So every application upgrade brings Homebrew along, and for
            # Homebrew's own row it is the whole upgrade: the applications each have a row of their own.
            #
            # Homebrew serializes `brew update` with a lockf on its prefix, and a second one fails at
            # once ("Another `brew update` process is already running") rather than waiting. The
            # agent only ever runs one patch at a time, so the holder is something else on the Mac —
            # the user's own brew, a `brew autoupdate` job, brew's auto-update inside a terminal
            # `brew install` — and it finishes in a minute or so. Failing on it turned that into a
            # patch failure that the next cycle would simply have succeeded at, so wait it out,
            # bounded, and fail immediately on anything else.
            brew_update() {
              local attempt output
              for attempt in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
                if output=$(brew update 2>&1); then
                  printf '%s\n' "$output"
                  return 0
                fi
                printf '%s\n' "$output" >&2
                case "$output" in
                  *"process is already running"*) ;;
                  *) return 1 ;;
                esac
                echo "another brew update holds Homebrew's lock; retrying in 30s (attempt $attempt of 20)" >&2
                sleep 30
              done
              return 1
            }

            brew_update

            if is_homebrew_itself; then
              exit 0
            fi

            brew upgrade "$APP_NAME"
            """;
    }
}
