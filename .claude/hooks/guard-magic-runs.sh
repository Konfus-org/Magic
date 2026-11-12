#!/usr/bin/env bash
# PreToolUse on shell commands: refuses a command that starts Magic other than through tools/Scripts/Run.ps1 or the
# scripts that run it themselves, which name its window for the session (AGENTS.md, Commands). Magic's own commands
# that open nothing, such as new and --help, pass. During another branch's profiling turn it also refuses a build or a
# test run (tools/Scripts/Profiling.ps1). Pure text and file checks, because it runs before every shell command.
input=$(cat)
command=$(printf '%s' "$input" | sed -n -E 's/.*"command"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
[ -z "$command" ] && exit 0

# Undo the JSON escapes: a backslash becomes /, as every path is compared with /, and a line break ends a command.
command=${command//\\\\/$'\x01'}
command=${command//\\n/;}
command=${command//\\t/ }
command=${command//\\\"/\"}
command=${command//\\//}
command=${command//$'\x01'//}
command=${command,,}

opens_nothing='^[[:space:]]*["'\'']?[[:space:]]*(--[[:space:]]+)?(new|--help|-h|help|--version)([^a-z0-9_-]|$)'
# What a command does with Magic: 1 starts it, 0 does not.
starts_magic() {
    local part=$1 after
    # Magic's program, by its path in the build output, anywhere in the command.
    if [[ $part =~ build/bin/[^/[:space:]\"\']+/magic(\.exe|\.dll)?([^a-z0-9_./-]|$) ]]; then
        after=${part#*"${BASH_REMATCH[0]}"}
        [[ ${BASH_REMATCH[2]} =~ [[:space:]] ]] && after=" $after"
        [[ $after =~ $opens_nothing ]] || return 0
    fi
    # Magic by its name alone as the first word: with nothing after it, or with an option or a project after it.
    if [[ $part =~ ^[[:space:]]*(\&[[:space:]]*)?[\"\']?(\./)?magic(\.exe)?[\"\']?([[:space:]]+([^[:space:]]+))?([[:space:]]|$) ]]; then
        after=${BASH_REMATCH[5]}
        [ -z "$after" ] && return 0
        [[ $after =~ ^(open|-|.*\.magic) ]] && ! [[ " $after" =~ $opens_nothing ]] && return 0
    fi
    [[ $part =~ (^|[^a-z0-9_./-])magic(\.exe)?[\"\']?[[:space:]]+open([^a-z0-9_]|$) ]] && return 0
    # dotnet run or dotnet watch, unless it runs a project that is not the engine or a sample, or a command that opens
    # nothing.
    if [[ $part =~ (^|[^a-z0-9_./-])dotnet[[:space:]]+(watch[[:space:]]+)?run([^a-z0-9_-]|$) ]]; then
        [[ $part =~ [[:space:]]--[[:space:]]+(new|--help|-h|help|--version)([^a-z0-9_-]|$) ]] && return 1
        [[ $part =~ --project[[:space:]=]+[\"\']?([^[:space:]\"\']+) ]] || return 0
        [[ ${BASH_REMATCH[1]} =~ src/core|samples/|magic\.core ]] && return 0
    fi

    return 1
}

# The profiling turn of another branch that has not ended: its holder and end time, read from the file that
# tools/Scripts/Profiling.ps1 writes in the git folder every worktree shares. Empty when there is none.
other_turn() {
    local dir gitdir common holder='' name='' until=0 line
    dir=$(printf '%s' "$input" | sed -n -E 's/.*"cwd"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
    dir=${dir//\\\\//}
    dir=${dir//\\//}
    [[ $dir =~ ^/([a-zA-Z])/(.*)$ ]] && dir="${BASH_REMATCH[1]}:/${BASH_REMATCH[2]}"
    while [ -n "$dir" ] && [ ! -e "$dir/.git" ]; do
        [ "${dir%/*}" = "$dir" ] && return
        dir=${dir%/*}
    done
    [ -n "$dir" ] || return
    if [ -f "$dir/.git" ]; then
        gitdir=$(sed -n 's/^gitdir: *//p' "$dir/.git")
        gitdir=${gitdir//\\//}
        common=$(< "$gitdir/commondir")
        [[ $common = /* || $common = ?:/* ]] || common="$gitdir/$common"
    else
        common="$dir/.git"
    fi
    [ -f "$common/agent-profiling" ] || return
    while IFS= read -r line || [ -n "$line" ]; do
        case $line in
            holder:*) holder=${line#holder:}; holder=${holder# } ;;
            branch:*) name=${line#branch:}; name=${name# } ;;
            until:*) until=${line#until:}; until=${until// /} ;;
        esac
    done < "$common/agent-profiling"
    holder=${holder,,}
    dir=${dir,,}
    [ -n "$holder" ] && [[ $until =~ ^[0-9]+$ ]] && [ "$until" -gt "$(date +%s)" ] || return
    # The holder's own command passes: one run in its worktree, or one that names its worktree.
    [ "$dir" = "$holder" ] || [[ $dir = "$holder"/* ]] || [[ $command = *"$holder"* ]] && return
    printf '%s until %s' "${name:-another worktree}" "$(date -d "@$until" +%H:%M 2>/dev/null || printf '%s' "$until")"
}
builds='(^|[^a-z0-9_./-])(dotnet[[:space:]]+(build|test|run|watch|publish|pack|msbuild)|msbuild)([^a-z0-9_-]|$)'

deny() {
    local reason=$1
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}' "${reason//\"/\\\"}"
    exit 0
}

# Each command of a line or a script on its own: split at ; & | and line breaks.
IFS=$'\x02' read -r -d '' -a parts < <(printf '%s' "$command" | tr ';&|' '\002\002\002'; printf '\0')
turn=unread
for part in "${parts[@]}"; do
    if [[ $part =~ $builds ]]; then
        [ "$turn" = unread ] && turn=$(other_turn)
        [ -n "$turn" ] && deny "The profiling turn of $turn is running, and a build or a test run would disturb it. Build with tools/Scripts/Validate.ps1 -Only Build, which runs at idle priority and does not wait, or wait until the turn ends (tools/Scripts/Profiling.ps1 -Wait)."
    fi
    starts_magic "$part" || continue
    reason="Start Magic only with tools/Scripts/Run.ps1, or Screenshot.ps1 and Validate.ps1, which name its window for this session (AGENTS.md, Commands). For example: tools/Scripts/Run.ps1 -Project samples/Forest/Forest.magic, or tools/Scripts/Run.ps1 -Headless -Commands 'wait loaded', 'exit'."
    deny "$reason"
done
exit 0
