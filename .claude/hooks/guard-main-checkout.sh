#!/usr/bin/env bash
# PreToolUse on file edits: refuses an edit to a file in Magic's main checkout (AGENTS.md, Worktrees). Pure path checks,
# no git and no PowerShell, because it runs before every edit. MAGIC_ALLOW_MAIN_EDITS=1 turns it off for a session.
[ "$MAGIC_ALLOW_MAIN_EDITS" = "1" ] && exit 0
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

root=$(find_git_root "${path%/*}")
# A linked worktree has a .git file; only a .git folder is a main checkout.
[ -n "$root" ] && [ -d "$root/.git" ] || exit 0

# The main checkout of the repository this session runs in, even when it was started in a worktree.
project=${CLAUDE_PROJECT_DIR//\\//}
project_root=$(find_git_root "$project")
[ -n "$project_root" ] || exit 0
if [ -f "$project_root/.git" ]; then
    gitdir=$(sed -n 's/^gitdir: *//p' "$project_root/.git")
    gitdir=${gitdir//\\//}
    project_root=${gitdir%%/.git/*}
fi
[ "${root,,}" = "${project_root,,}" ] || exit 0

reason="$path is in the main checkout, where agents never edit (AGENTS.md, Worktrees). Make the task's worktree first, for example: git worktree add ../Magic-worktrees/<type>/<feature> -b <type>/<feature>, then edit the file there. The user can allow main-checkout edits for a session by starting Claude Code with MAGIC_ALLOW_MAIN_EDITS=1."
reason=${reason//\"/\\\"}
printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}' "$reason"
exit 0
