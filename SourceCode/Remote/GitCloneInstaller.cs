namespace AllInOneInstaller.Remote;

public enum CloneTargetKind { Missing, SameRepository, OtherContent }

/// <summary>The target folder as found before cloning.</summary>
public sealed record CloneState(
    CloneTargetKind Kind,
    string? Origin,
    string? Branch,
    string? Commit,
    int ChangedFiles,
    int Behind);

/// <summary>Clones a GitHub repository on the server, or updates an existing clone.</summary>
public static class GitCloneInstaller
{
    // Git separates progress updates with carriage returns. Keep only the meaningful lines.
    private const string QuietProgress =
        "tr '\\r' '\\n' | awk '/^Cloning|done\\.|^Updating files: .*done|^(warning|fatal|error)|Already up to date|Fast-forward|files? changed/ {print; fflush()}'";

    public static async Task<CloneState> InspectAsync(ServerConnection connection, string url, string target,
        CancellationToken token = default)
    {
        string dir = ServerConnection.ShellQuote(target);
        string script = $$"""
            d={{dir}}
            if [ ! -d "$d" ] || [ -z "$(ls -A "$d" 2>/dev/null)" ]; then echo "kind=missing"; exit 0; fi
            # safe.directory because the folder may belong to another account. The emulator step gives it to "emulator".
            origin=$(git -c safe.directory="$d" -C "$d" remote get-url origin 2>/dev/null)
            echo "origin=$origin"
            if [ -z "$origin" ] || ! git -c safe.directory="$d" -C "$d" rev-parse --show-toplevel 2>/dev/null | grep -qx "$(readlink -f "$d")"; then echo "kind=other"; exit 0; fi
            echo "kind=repo"
            echo "branch=$(git -c safe.directory="$d" -C "$d" rev-parse --abbrev-ref HEAD 2>/dev/null)"
            echo "commit=$(git -c safe.directory="$d" -C "$d" log -1 --format='%h %cd %s' --date=short 2>/dev/null)"
            echo "changed=$(git -c safe.directory="$d" -C "$d" status --porcelain --untracked-files=no 2>/dev/null | wc -l)"
            timeout 30 git -c safe.directory="$d" -C "$d" fetch --quiet origin 2>/dev/null
            echo "behind=$(git -c safe.directory="$d" -C "$d" rev-list --count HEAD..@{u} 2>/dev/null || echo 0)"
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var values = new ScriptValues(result.Output);

        CloneTargetKind kind = values["kind"] switch
        {
            "missing" => CloneTargetKind.Missing,
            "repo" when SameRepository(values["origin"], url) => CloneTargetKind.SameRepository,
            _ => CloneTargetKind.OtherContent
        };
        return new CloneState(kind, values["origin"], values["branch"], values["commit"],
            int.TryParse(values["changed"], out int changed) ? changed : 0,
            int.TryParse(values["behind"], out int behind) ? behind : 0);
    }

    // https and SSH URLs, with or without ".git", match the same repository.
    public static bool SameRepository(string? origin, string url)
    {
        static string Normalize(string value)
        {
            value = value.Trim().TrimEnd('/').ToLowerInvariant();
            if (value.EndsWith(".git", StringComparison.Ordinal)) value = value[..^4];
            int at = value.IndexOf("github.com", StringComparison.Ordinal);
            return at >= 0 ? value[at..].Replace(':', '/') : value;
        }
        return origin is not null && Normalize(origin) == Normalize(url);
    }

    /// <summary>Renames the folder to &lt;folder&gt;.bak-&lt;time&gt; and returns the new path.</summary>
    public static async Task<string> MoveAsideAsync(ServerConnection connection, string target, Action<string> log,
        CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            d={{ServerConnection.ShellQuote(target)}}
            backup="$d.bak-$(date +%Y%m%d-%H%M%S)"
            mv "$d" "$backup"
            echo "  $d moved to $backup"
            echo "BACKUP=$backup"
            """;
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, "Moving the folder aside failed", ["BACKUP"], token);
        return result["BACKUP"] ?? "";
    }

    /// <summary>
    /// Sets $branch to the branch the remote really has, matched without case: one repository writes it as
    /// "dev" and another as "Dev". Empty when no branch was chosen, so the repository's own default is used.
    /// </summary>
    private static string ResolveBranch(string? branch, string listRefs) => branch is null ? "branch=\n" : $$"""
        want={{ServerConnection.ShellQuote(branch)}}
        branch=$({{listRefs}} | grep -ix "$want" | head -1 || true)
        [ -n "$branch" ] || fail "Branch $want is not in this repository."

        """;

    /// <summary>Installs git if needed, clones the branch (null: the repository's default), and returns the branch and commit.</summary>
    public static Task<string> CloneAsync(ServerConnection connection, string url, string target, string? branch,
        Action<string> log, CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            set -o pipefail
            if ! command -v git >/dev/null 2>&1; then
              echo "> Installing git"
              apt_update
              apt-get install -y git
            fi
            d={{ServerConnection.ShellQuote(target)}}
            u={{ServerConnection.ShellQuote(url)}}
            install -d -m 755 "$(dirname "$d")"
            {{ResolveBranch(branch, "git ls-remote --heads \"$u\" | sed 's#.*refs/heads/##'")}}
            echo "> git clone${branch:+ --branch $branch} {{url}} $d"
            git clone ${branch:+--branch "$branch"} --progress "$u" "$d" 2>&1 | {{QuietProgress}}
            echo "COMMIT=$(git -c safe.directory="$d" -C "$d" rev-parse --abbrev-ref HEAD) $(git -c safe.directory="$d" -C "$d" log -1 --format='%h %cd %s' --date=short)"
            """;
        return CommitAfterAsync(connection, script, log, "Cloning failed", token);
    }

    /// <summary>
    /// Fast-forwards the clone, refusing if tracked files were changed. Switches to <paramref name="branch"/>
    /// first when the clone is on another one (null: stay on the branch it is on). Returns the branch and commit.
    /// </summary>
    public static Task<string> UpdateAsync(ServerConnection connection, string target, string? branch, Action<string> log,
        CancellationToken token = default)
    {
        string script = RemoteScript.Prelude + $$"""
            set -o pipefail
            d={{ServerConnection.ShellQuote(target)}}
            # With AtomCMS the Octane step adds --base=/client/ to package.json. Undo only that for the pull,
            # since the Octane step and build.sh put it back.
            if [ -f "$d/package.json" ] && grep -q -- '--base=' "$d/package.json" &&
               sed -E 's#("(start|build)": "vite) --base=[^ "]+#\1#' "$d/package.json" | cmp -s - <(git -c safe.directory="$d" -C "$d" show HEAD:package.json 2>/dev/null); then
              git -c safe.directory="$d" -C "$d" checkout -- package.json
              echo "  package.json: --base for /client/ undone for the pull (the Octane step sets it again)"
            fi
            # The Octane update tool writes a new yarn.lock, and the pull may bring its own.
            if git -c safe.directory="$d" -C "$d" ls-files --error-unmatch yarn.lock >/dev/null 2>&1 &&
               [ -n "$(git -c safe.directory="$d" -C "$d" status --porcelain -- yarn.lock)" ]; then
              git -c safe.directory="$d" -C "$d" checkout -- yarn.lock
              echo "  yarn.lock: the locally generated one is replaced by the repository's for the pull"
            fi
            changed=$(git -c safe.directory="$d" -C "$d" status --porcelain --untracked-files=no)
            if [ -n "$changed" ]; then
              echo "$changed" | head -20
              fail "Files tracked by git were changed in $d. Commit or undo those changes first, so they are not lost."
            fi
            echo "> git fetch origin"
            git -c safe.directory="$d" -C "$d" fetch --quiet --prune origin
            {{ResolveBranch(branch, "git -c safe.directory=\"$d\" -C \"$d\" for-each-ref --format='%(refname:lstrip=3)' refs/remotes/origin")}}
            current=$(git -c safe.directory="$d" -C "$d" rev-parse --abbrev-ref HEAD)
            if [ -n "$branch" ] && [ "$(echo "$current" | tr 'A-Z' 'a-z')" != "$(echo "$branch" | tr 'A-Z' 'a-z')" ]; then
              echo "> git checkout $branch  (was $current)"
              if git -c safe.directory="$d" -C "$d" show-ref --verify --quiet "refs/heads/$branch"; then
                git -c safe.directory="$d" -C "$d" checkout "$branch"
              else
                git -c safe.directory="$d" -C "$d" checkout --track "origin/$branch"
              fi
            fi
            echo "> git pull --ff-only"
            git -c safe.directory="$d" -C "$d" pull --ff-only --progress 2>&1 | {{QuietProgress}}
            echo "COMMIT=$(git -c safe.directory="$d" -C "$d" rev-parse --abbrev-ref HEAD) $(git -c safe.directory="$d" -C "$d" log -1 --format='%h %cd %s' --date=short)"
            """;
        return CommitAfterAsync(connection, script, log, "Updating failed", token);
    }

    private static async Task<string> CommitAfterAsync(ServerConnection connection, string script, Action<string> log,
        string failure, CancellationToken token)
    {
        ScriptValues result = await RemoteScript.RunCapturingAsync(connection, script, log, failure, ["COMMIT"], token);
        return result["COMMIT"] ?? "";
    }
}
