/**
 * @file parser.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Implementation of magic/commands/parser.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/commands/parser.h"
#include "magic/collections/string.h"
#include "magic/commands/registry.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"

/**
 * @brief Whether an argv slot starts a command: a dash with something after
 * it, so a bare - stays an ordinary argument.
 */
static b8 mg_is_cmd(const char* arg) {
    return arg[0] == '-' && arg[1] != '\0';
}

mg_cmd_iterator mg_cmd_iterator_construct(void) {
    mg_cmd_iterator it = {
        .index = 1,
        .curr_args = mg_cstr_array_construct(NULL, 0),
        .curr = NULL,
    };
    return it;
}

b8 mg_get_cmd(mg_cmd_iterator* it, const int argc, const char** argv) {
    MG_ASSERT_RETURN_VAL_IF(it != NULL, FALSE, "it must not be NULL");
    it->curr_args = mg_cstr_array_construct(NULL, 0);
    it->curr = NULL;
    if (argv == NULL || it->index >= argc) {
        return FALSE;
    }

    const char* arg = argv[it->index];
    if (!mg_is_cmd(arg)) {
        return FALSE; /* the first non-command argument ends the scan */
    }
    it->index++;

    /* The name is whatever follows the dashes, still sitting in argv: -h
     * hands back "h", --help hands back "help", and matching the two up is
     * the caller's business. */
    it->curr = arg[1] == '-' ? arg + 2 : arg + 1;

    /* The arguments already sit side by side in argv, right behind the
     * command, so the array is constructed over argv itself running up to
     * the next command: nothing is stored or copied. */
    const char** args = argv + it->index;
    len count = 0;
    while (it->index < argc && !mg_is_cmd(argv[it->index])) {
        it->index++;
        count++;
    }
    it->curr_args = mg_cstr_array_construct(args, count);

    /* A constructed array starts empty, but this one's slots are argv's own,
     * already live: the count IS the capacity, with nothing to push. */
    it->curr_args.count = count;

    return TRUE;
}

b8 mg_cmd_is(const char* name, const char* long_name) {
    const b8 is_short = name[0] == long_name[0] && name[1] == '\0';
    return is_short || mg_str_eq(mg_view_cstr(name), mg_view_cstr(long_name));
}

u32 mg_match_cmd(const char* name) {
    if (mg_cmd_is(name, "help")) {
        return MG_CMD_CODE_HELP;
    }
    if (mg_cmd_is(name, "name")) {
        return MG_CMD_CODE_NAME;
    }
    if (mg_cmd_is(name, "dirs")) {
        return MG_CMD_CODE_DIRS;
    }
    if (mg_cmd_is(name, "window")) {
        return MG_CMD_CODE_WINDOW;
    }
    return MG_CMD_CODE_NONE;
}

void mg_parse_cmd_line(const int argc, const char** argv) {
    mg_cmd_iterator it = mg_cmd_iterator_construct();
    while (mg_get_cmd(&it, argc, argv)) {
        const u32 code = mg_match_cmd(it.curr);
        if (code == MG_CMD_CODE_NONE) {
            MG_WARN("unknown option: %s", it.curr);
            continue;
        }

        const mg_cmd cmd = {.code = code, .args = it.curr_args};
        mg_run_cmd(&cmd);
    }
}
