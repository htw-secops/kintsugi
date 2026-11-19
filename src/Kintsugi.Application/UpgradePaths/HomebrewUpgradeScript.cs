namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for everything Homebrew
/// manages, Homebrew itself included. Shared between the "Find Upgrade Paths" research flow
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
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
              exit 1
            }

            APP_NAME=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) APP_NAME="$2"; shift 2 ;;
                --appId) shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
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

            # --update mode: runs on the managed Mac itself, where `brew` actually exists.
            if ! command -v brew >/dev/null 2>&1; then
              echo "homebrew is not installed" >&2
              exit 1
            fi

            # ---- what is really installed -------------------------------------------------------------
            #
            # Homebrew's own records are not the truth about a Mac. An app that updates itself (Chrome) moves
            # on while Homebrew still records the version it installed, and `brew upgrade` can then fail on a
            # stale artifact for an app that is already current. So before and after an upgrade this script
            # reads the version that is actually on the disk and compares numbers, not strings.

            # The first dotted or hyphenated run of digits: "157.0.8103.79,abc" and "1.2.3_1" give "157.0.8103.79"
            # and "1.2.3". A cask's ",<revision>" suffix and trailing words are not version components.
            version_core() {
              printf '%s' "$1" | sed -E 's/,.*$//' | grep -oE '[0-9]+([.-][0-9]+)*' | head -1 || true
            }

            # Succeeds when version $1 is the same as, or newer than, version $2, part by part as numbers
            # ("1.10" is newer than "1.9"). Fails when either one has no number in it: a version that cannot be
            # read proves nothing. Written for the bash 3.2 that macOS ships.
            version_at_least() {
              local have want n i x y
              local -a h w
              have=$(version_core "$1")
              want=$(version_core "$2")
              [ -n "$have" ] && [ -n "$want" ] || return 1
              IFS='.-' read -r -a h <<< "$have"
              IFS='.-' read -r -a w <<< "$want"
              n=${#h[@]}
              if [ "${#w[@]}" -gt "$n" ]; then n=${#w[@]}; fi
              i=0
              while [ "$i" -lt "$n" ]; do
                x=$((10#${h[$i]:-0}))
                y=$((10#${w[$i]:-0}))
                if [ "$x" -gt "$y" ]; then return 0; fi
                if [ "$x" -lt "$y" ]; then return 1; fi
                i=$((i + 1))
              done
              return 0
            }

            # What brew knows about this name as a cask, as one line of JSON. Empty for a formula (or nothing).
            # brew pretty-prints this JSON over a hundred lines, with the "app" key on one line and the bundle
            # name on the next, so the newlines are removed here: every grep below reads one line. Without it
            # the bundle name was never found, the auto-update early exit never fired, and the version fell
            # back to Homebrew's stale record.
            CASK_JSON=""
            CASK_JSON=$(brew info --cask --json=v2 "$APP_NAME" </dev/null 2>/dev/null | tr -d '\n\r') || CASK_JSON=""

            # `case` rather than a pipe into `grep -q`: with pipefail, grep closing the pipe early can make the
            # printf fail with SIGPIPE and turn a match into a "no".
            cask_auto_updates() {
              case "$CASK_JSON" in
                *'"auto_updates":true'* | *'"auto_updates": true'*) return 0 ;;
              esac
              return 1
            }

            # The cask's app bundle name, e.g. "Google Chrome.app". Empty when the cask installs no .app.
            cask_app_name() {
              printf '%s' "$CASK_JSON" | grep -oE '"app" *: *\[ *"[^"]*"' | head -1 | sed -E 's/.*\[ *"//; s/"$//' || true
            }

            # CFBundleShortVersionString from the app's Info.plist, in the places apps are installed.
            app_bundle_version() {
              local dir plist v
              for dir in "/Applications" "$HOME/Applications"; do
                plist="$dir/$1/Contents/Info.plist"
                [ -f "$plist" ] || continue
                v=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist" 2>/dev/null) || v=""
                if [ -z "$v" ]; then
                  v=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$plist" 2>/dev/null) || v=""
                fi
                if [ -n "$v" ]; then
                  printf '%s' "$v"
                  return 0
                fi
              done
              return 1
            }

            # A cask version can carry a build after a comma ("4.37.1,178610"). Only a purely numeric one is a
            # build; "157.0.8103.79,abc" has a revision suffix that is not.
            target_build() {
              local b
              case "$1" in
                *,*) b=${1#*,}; b=${b%%,*} ;;
                *) return 1 ;;
              esac
              case "$b" in
                '' | *[!0-9]*) return 1 ;;
              esac
              printf '%s' "$b"
            }

            # The app's CFBundleVersion when it is a plain build number (digits only). Nothing otherwise: a
            # CFBundleVersion shaped like a version ("4.37.1") is not numbered like the cask's build.
            app_bundle_build() {
              local dir plist v
              for dir in "/Applications" "$HOME/Applications"; do
                plist="$dir/$1/Contents/Info.plist"
                [ -f "$plist" ] || continue
                v=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$plist" 2>/dev/null) || v=""
                case "$v" in
                  '' | *[!0-9]*) return 1 ;;
                esac
                printf '%s' "$v"
                return 0
              done
              return 1
            }

            # Succeeds when the app bundle $1 is at least $2: the version, and also the build when the target
            # carries one that the bundle can be compared with. An older build of the same marketing version
            # is not current, which comparing the version alone would call current.
            bundle_is_current() {
              local app=$1 target=$2 v tb ab
              v=$(app_bundle_version "$app") || return 1
              version_at_least "$v" "$target" || return 1
              # A strictly newer version needs no build comparison: builds are only comparable within one version.
              version_at_least "$target" "$v" || return 0
              tb=$(target_build "$target") || return 0
              ab=$(app_bundle_build "$app") || return 0
              [ "$((10#$ab))" -ge "$((10#$tb))" ]
            }

            # The version on this Mac: the app bundle's own when the cask has one, else brew's record.
            installed_version() {
              local app v kind
              app=$(cask_app_name)
              if [ -n "$app" ]; then
                v=$(app_bundle_version "$app") || v=""
                if [ -n "$v" ]; then
                  printf '%s' "$v"
                  return 0
                fi
              fi
              for kind in --cask --formula; do
                v=$(brew list "$kind" --versions "$APP_NAME" </dev/null 2>/dev/null | awk 'NF { print $NF }' | tail -1) || v=""
                if [ -n "$v" ]; then
                  printf '%s' "$v"
                  return 0
                fi
              done
              return 1
            }

            # The version this run is trying to reach. Empty when it cannot be read; nothing below then
            # turns a failure into a success, because there is nothing to compare with.
            TARGET=$(latest_version 2>/dev/null) || TARGET=""

            installed_is_current() {
              local installed app
              [ -n "$TARGET" ] || return 1
              app=$(cask_app_name)
              if [ -n "$app" ] && app_bundle_version "$app" >/dev/null; then
                bundle_is_current "$app" "$TARGET"
                return
              fi
              installed=$(installed_version) || return 1
              version_at_least "$installed" "$TARGET"
            }

            # An app that updates itself (`auto_updates true`) is checked first, on the real bundle, and left
            # alone when it is already current. Upgrading it would only fight its own updater and, when
            # Homebrew's record is stale, fail on an artifact that is already in place.
            if ! is_homebrew_itself && [ -n "$TARGET" ] && cask_auto_updates; then
              bundle_app=$(cask_app_name)
              if [ -n "$bundle_app" ]; then
                bundle_version=$(app_bundle_version "$bundle_app") || bundle_version=""
                if [ -n "$bundle_version" ] && bundle_is_current "$bundle_app" "$TARGET"; then
                  echo "$APP_NAME is already current: the installed app is $bundle_version and the latest is $TARGET (it updates itself, so Homebrew's record is not consulted)"
                  exit 0
                fi
              fi
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

            # ---- the upgrade, and the result of its LAST attempt --------------------------------------------
            #
            # This runs as the console user, unattended. Nothing may wait for a person: stdin is /dev/null,
            # NONINTERACTIVE is set, and SUDO_ASKPASS is a program that fails at once, so a cask that needs
            # sudo ends the attempt with a clear error instead of hanging the patch cycle on a prompt.
            # `brew update` has just run, so the upgrade does not run it again. Downloads get brew's own
            # retries (HOMEBREW_CURL_RETRIES) on top of the bounded retry below.
            export NONINTERACTIVE=1
            export HOMEBREW_NO_AUTO_UPDATE=1
            export HOMEBREW_CURL_RETRIES=3

            ASKPASS=$(mktemp "${TMPDIR:-/tmp}/kintsugi-askpass.XXXXXX")
            trap 'rm -f "$ASKPASS"' EXIT
            printf '#!/bin/sh\necho "a password was requested and there is nobody to answer" >&2\nexit 1\n' > "$ASKPASS"
            chmod 700 "$ASKPASS"
            export SUDO_ASKPASS="$ASKPASS"

            # A known brew failure, as a short reason. Empty for anything else, which is treated as transient
            # (a timed-out download, a flaky mirror) and retried. A known one never is: running the same
            # command again cannot fix a stale artifact, a missing permission or the wrong user.
            classify_failure() {
              case "$1" in
                *"already an App at"*) echo "Stale Caskroom artifact (check App Management permission)" ;;
                *"Running Homebrew as root"* | *"running Homebrew as root"*) echo "Wrong execution context: Homebrew will not run as root" ;;
                *"a password is required"* | *"a password was requested"* | *"no askpass program"* | *"a terminal is required"* | *"no tty present"* | *"Password:"*) echo "Requires admin privileges" ;;
                *) echo "" ;;
              esac
            }

            UPGRADE_REASON=""

            # Runs the upgrade up to three times, backing off between attempts, and returns the exit status of
            # the last one it made. An earlier failure that a later attempt got past counts for nothing: it was
            # the first download that timed out, the second one installed the app, and the run used to report
            # the first one's exit status.
            upgrade_with_retries() {
              local attempt=1 max=3 output status
              while :; do
                status=0
                output=$(brew upgrade "$APP_NAME" </dev/null 2>&1) || status=$?
                printf '%s\n' "$output"
                if [ "$status" -eq 0 ]; then
                  return 0
                fi
                UPGRADE_REASON=$(classify_failure "$output")
                if [ -n "$UPGRADE_REASON" ]; then
                  echo "kintsugi: $UPGRADE_REASON" >&2
                  return "$status"
                fi
                if installed_is_current; then
                  return 0
                fi
                if [ "$attempt" -ge "$max" ]; then
                  return "$status"
                fi
                echo "kintsugi: brew upgrade failed (exit $status); retrying in $((attempt * 30))s (attempt $((attempt + 1)) of $max)" >&2
                sleep $((attempt * 30))
                attempt=$((attempt + 1))
              done
            }

            status=0
            upgrade_with_retries || status=$?
            if [ "$status" -eq 0 ]; then
              exit 0
            fi

            # The upgrade reported a failure. What is on the disk decides: at or past the target is a success,
            # whatever went wrong on the way. (A success exit is never second-guessed above: the target comes
            # from Homebrew's public API and can be ahead of the index this Mac has.)
            if installed_is_current; then
              installed=$(installed_version) || installed=""
              echo "kintsugi: brew exited $status but $APP_NAME is installed at $installed (target $TARGET); reporting success" >&2
              exit 0
            fi
            installed=$(installed_version) || installed=""
            echo "kintsugi: upgrade of $APP_NAME failed (exit $status): ${UPGRADE_REASON:-unclassified}; installed ${installed:-unknown}, target ${TARGET:-unknown}" >&2
            exit "$status"
            """;
    }
}
