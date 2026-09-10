/**
 * @file parser.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The command line half of commands: a scan walks argv one command at
 * a time, handing back each command's name and its arguments through an
 * iterator. What a name means -- which code it maps to, whose handler runs --
 * is the registry's side, in magic/commands/registry.h.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/array.h"
#include "magic/export.h"
#include "magic/primitives.h"

/**
 * @brief A borrowed C string, under one name: what an argv slot holds. The
 * typedef is load-bearing for the mint below, where a pasted const char*
 * would double its const -- through a typedef the qualifiers compose.
 */
typedef const char* mg_cstr;

/* The array of C strings, minted here where the scan hands argv slots back
 * through one. The scan constructs it over argv itself, so it is borrowed
 * and never freed. */
MG_DEFINE_NAMED_ARRAY(mg_cstr, mg_cstr_array, NULL);

/**
 * @brief One step of a scan over argv. Start from mg_cmd_iterator_construct
 * and call mg_get_cmd until it returns false; the whole iterator is plain
 * stack data borrowing from argv, so there is nothing to free after.
 */
typedef struct mg_cmd_iterator {
    int index; /**< The next argv slot to read; starts at 1, past the program name. */
    mg_cstr_array curr_args; /**< The last command's arguments: an array over
                              * argv itself, already full, borrowed. */
    const char* curr; /**< The last command's name past its dashes, borrowed
                       * from argv: -h hands back "h", --help hands back "help". */
} mg_cmd_iterator;

/**
 * @brief An iterator at the start of a scan: the first command's argv slot,
 * with no values yet. Allocates nothing, and neither does anything after it;
 * the scan's whole footprint is this struct.
 * @return The iterator, by value; every scan starts from a fresh one.
 */
MG_EXPORT mg_cmd_iterator mg_cmd_iterator_construct(void);

/**
 * @brief Reads the next command from argv into it, pointing it->curr_args at
 * everything between the command and the next one, where it already sits in
 * argv, and it->curr at the command's text past its dashes. The caller
 * decides what any name means, and checks it->curr_args.count before
 * trusting any arguments slot.
 * @param it The scan's position and result; each call discards the arguments
 * the last one handed back.
 * @param argc The count main was given.
 * @param argv The arguments main was given.
 * @return False when argv is exhausted or the next argument is no command,
 * which ends the scan.
 */
MG_EXPORT b8 mg_get_cmd(mg_cmd_iterator* it, int argc, const char** argv);

/**
 * @brief Whether a scanned name means the given command: its long name in
 * full, or exactly its first letter, so --help and -h are the same command.
 * @param name The scanned name, past its dashes.
 * @param long_name The command's full name.
 * @return True on either spelling, false on everything else.
 */
MG_EXPORT b8 mg_cmd_is(const char* name, const char* long_name);

/**
 * @brief Maps a scanned name onto the code of the built-in command it
 * spells: a u32, like a code everywhere it travels. A user's own commands
 * match their own names the same way, through mg_cmd_is.
 * @param name The scanned name, past its dashes.
 * @return The matched code, or MG_CMD_CODE_NONE.
 */
MG_EXPORT u32 mg_match_cmd(const char* name);

/**
 * @brief Scans the whole command line and runs every command it finds
 * through the registry: mg_get_cmd, mg_match_cmd and mg_run_cmd in one call.
 * A name matching no command logs a warning and the scan moves on.
 * @param argc The count main was given.
 * @param argv The arguments main was given.
 */
MG_EXPORT void mg_parse_cmd_line(int argc, const char** argv);
