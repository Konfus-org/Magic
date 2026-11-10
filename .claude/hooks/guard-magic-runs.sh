#!/usr/bin/env bash
# PreToolUse on shell commands: refuses a command that starts Magic other than through tools/Scripts/Run.ps1 or the
# scripts that run it themselves, which name its window for the session (AGENTS.md, Commands). Magic's own commands
# that open nothing, such as new and --help, pass. Pure text checks, because it runs before every shell command.
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

# Each command of a line or a script on its own: split at ; & | and line breaks.
IFS=$'\x02' read -r -d '' -a parts < <(printf '%s' "$command" | tr ';&|' '\002\002\002'; printf '\0')
for part in "${parts[@]}"; do
    starts_magic "$part" || continue
    reason="Start Magic only with tools/Scripts/Run.ps1, or Screenshot.ps1 and Validate.ps1, which name its window for this session (AGENTS.md, Commands). For example: tools/Scripts/Run.ps1 -Project samples/Forest/Forest.magic, or tools/Scripts/Run.ps1 -Headless -Commands 'wait loaded', 'exit'."
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}' "$reason"
    exit 0
done
exit 0
