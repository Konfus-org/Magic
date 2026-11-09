#!/usr/bin/env bash
# PreToolUse on file edits: refuses an edit to a file in Magic's main checkout, and one inside another session's claim
# that this worktree's own claim doesn't cover (AGENTS.md, Worktrees). Pure file reads, no git and no PowerShell,
# because it runs before every edit. MAGIC_ALLOW_MAIN_EDITS=1 turns the main-checkout check off for a session.
input=$(cat)
path=$(printf '%s' "$input" | sed -n 's/.*"\(file_path\|notebook_path\)"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\2/p')
[ -z "$path" ] && exit 0
path=${path//\\\\//}
path=${path//\\//}

# The folder holding the nearest .git above a path, or nothing.
find_git_root() {
    local dir=$1 parent
    while :; do
        [ -e "$dir/.git" ] && { printf '%s' "$dir"; return; }
        parent=${dir%/*}
        [ "$parent" = "$dir" ] || [ -z "$parent" ] && return
        dir=$parent
    done
}

# Refuses the edit with a reason the agent is shown.
deny() {
    local reason=${1//\"/\\\"}
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}' "$reason"
    exit 0
}

root=$(find_git_root "${path%/*}")
[ -n "$root" ] || exit 0

# A main checkout has a .git folder; refuse an edit in the one of the repository this session runs in.
if [ -d "$root/.git" ]; then
    [ "$MAGIC_ALLOW_MAIN_EDITS" = "1" ] && exit 0
    project=${CLAUDE_PROJECT_DIR//\\//}
    project_root=$(find_git_root "$project")
    [ -n "$project_root" ] || exit 0
    if [ -f "$project_root/.git" ]; then
        gitdir=$(sed -n 's/^gitdir: *//p' "$project_root/.git")
        gitdir=${gitdir//\\//}
        project_root=${gitdir%%/.git/*}
    fi
    [ "${root,,}" = "${project_root,,}" ] || exit 0
    deny "$path is in the main checkout, where agents never edit (AGENTS.md, Worktrees). Make the task's worktree first, for example: git worktree add ../Magic-worktrees/<type>/<feature> -b <type>/<feature>, then edit the file there. The user can allow main-checkout edits for a session by starting Claude Code with MAGIC_ALLOW_MAIN_EDITS=1."
fi

# A linked worktree: its own git folder holds its HEAD, and the folder every worktree shares holds the claims.
gitdir=$(sed -n 's/^gitdir: *//p' "$root/.git")
gitdir=${gitdir//\\//}
[ -f "$gitdir/commondir" ] || exit 0
common=$(< "$gitdir/commondir")
common=${common//\\//}
[[ $common = /* || $common = ?:/* ]] || common="$gitdir/$common"
claims="$common/agent-claims"
[ -d "$claims" ] || exit 0
head=$(< "$gitdir/HEAD")
branch=${head#ref: refs/heads/}
file=${path#"$root"/}

# Whether a claim covers the file: it is one of the claim's paths, or under one of its folders. Sets claim_branch,
# claim_slice and claim_worktree.
covers() {
    local line key value entry entries hit=1
    claim_branch='' claim_slice='' claim_worktree=''
    while IFS= read -r line || [ -n "$line" ]; do
        key=${line%%:*}
        value=${line#*: }
        case $key in
            branch) claim_branch=$value ;;
            slice) claim_slice=$value ;;
            worktree) claim_worktree=$value ;;
            scope)
                IFS=',' read -ra entries <<< "$value"
                for entry in "${entries[@]}"; do
                    entry=${entry# }
                    [ -n "$entry" ] || continue
                    [ "${file,,}" = "${entry,,}" ] || [[ ${file,,} = "${entry,,}"/* ]] && hit=0
                done ;;
        esac
    done < "$1"
    return $hit
}

# The worktree's own claim lets the edit through, even where another claim overlaps it with the user's yes.
own="$claims/${branch//[\/\\:]/__}"
[ -f "$own" ] && covers "$own" && exit 0
for claim in "$claims"/*; do
    [ -f "$claim" ] && [ "$claim" != "$own" ] || continue
    covers "$claim" || continue
    [ -d "$claim_worktree" ] || continue
    deny "$file is in the claim of $claim_branch ($claim_slice), another session's slice (AGENTS.md, Worktrees). Ask the user whether to wait for that work, narrow this slice, or go ahead. With their yes, add the path to this slice's claim with tools/Scripts/Claim.ps1 and edit again."
done
exit 0
