/**
 * @file commands_test.cpp
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief magic/commands/parser.h and magic/commands/registry.h.
 * @version 1.0
 * @date 2026-09-09
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

/* gtest first: it drags in the std headers, and buffer.h's first/last macros
 * would otherwise expand inside std::pair's members. */
#include <gtest/gtest.h>

#include <magic.h>

namespace {

/* What the recording handler saw last; each test resets what it reads. */
u32 g_ran_code = 0;
len g_ran_arg_count = 0;

b8 record_cmd(void* listener, const mg_cmd* cmd) {
    (void)listener;
    g_ran_code = cmd->code;
    g_ran_arg_count = cmd->args.count;
    return TRUE;
}

b8 refuse_cmd(void* listener, const mg_cmd* cmd) {
    (void)listener;
    (void)cmd;
    return FALSE;
}

b8 bump_listener(void* listener, const mg_cmd* cmd) {
    (void)cmd;
    *static_cast<u32*>(listener) += 1;
    return TRUE;
}

TEST(CmdParser, ScansCommandsAndWindowsTheirArguments) {
    const char* argv[] = {"app", "--name", "Test", "-w", "800", "600", "0"};
    mg_cmd_iterator it = mg_cmd_iterator_construct();

    ASSERT_TRUE(mg_get_cmd(&it, 7, argv));
    EXPECT_STREQ(it.curr, "name");
    ASSERT_EQ(it.curr_args.count, 1u);
    EXPECT_STREQ(*mg_cstr_array_at(&it.curr_args, 0), "Test");

    ASSERT_TRUE(mg_get_cmd(&it, 7, argv));
    EXPECT_STREQ(it.curr, "w");
    ASSERT_EQ(it.curr_args.count, 3u);
    EXPECT_STREQ(*mg_cstr_array_at(&it.curr_args, 2), "0");

    EXPECT_FALSE(mg_get_cmd(&it, 7, argv));
}

TEST(CmdParser, APositionalArgumentEndsTheScan) {
    const char* argv[] = {"app", "positional", "--name", "Test"};
    mg_cmd_iterator it = mg_cmd_iterator_construct();
    EXPECT_FALSE(mg_get_cmd(&it, 4, argv));
}

TEST(CmdParser, MatchesFullNamesAndFirstLettersOnly) {
    EXPECT_TRUE(mg_cmd_is("help", "help"));
    EXPECT_TRUE(mg_cmd_is("h", "help"));
    EXPECT_FALSE(mg_cmd_is("he", "help"));
    EXPECT_FALSE(mg_cmd_is("herp", "help"));

    EXPECT_EQ(mg_match_cmd("window"), static_cast<u32>(MG_CMD_CODE_WINDOW));
    EXPECT_EQ(mg_match_cmd("w"), static_cast<u32>(MG_CMD_CODE_WINDOW));
    EXPECT_EQ(mg_match_cmd("nonsense"), static_cast<u32>(MG_CMD_CODE_NONE));
}

TEST(CmdParser, ParseCmdLineRunsWhatItScans) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);
    g_ran_code = 0;
    g_ran_arg_count = 0;

    mg_register_cmd_handler(MG_CMD_CODE_WINDOW, nullptr, record_cmd);
    const char* argv[] = {"app", "--window", "800", "600", "0"};
    mg_parse_cmd_line(5, argv);

    EXPECT_EQ(g_ran_code, static_cast<u32>(MG_CMD_CODE_WINDOW));
    EXPECT_EQ(g_ran_arg_count, 3u);

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

TEST(CmdRegistry, RunsTheHandlerRegisteredForACode) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);
    g_ran_code = 0;

    mg_register_cmd_handler(MG_CMD_CODE_NAME, nullptr, record_cmd);
    const mg_cmd cmd = {static_cast<u32>(MG_CMD_CODE_NAME), mg_cstr_array_construct(nullptr, 0)};

    EXPECT_TRUE(mg_run_cmd(&cmd));
    EXPECT_EQ(g_ran_code, static_cast<u32>(MG_CMD_CODE_NAME));

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

TEST(CmdRegistry, HandsTheListenerBackToItsHandler) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);

    u32 bumped = 0;
    mg_register_cmd_handler(MG_CMD_CODE_NAME, &bumped, bump_listener);
    const mg_cmd cmd = {static_cast<u32>(MG_CMD_CODE_NAME), mg_cstr_array_construct(nullptr, 0)};

    EXPECT_TRUE(mg_run_cmd(&cmd));
    EXPECT_EQ(bumped, 1u);

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

TEST(CmdRegistry, CustomCodesPastTheBuiltInsWork) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);
    g_ran_code = 0;

    const u32 custom = static_cast<u32>(MG_CMD_CODE_COUNT) + 41;
    mg_register_cmd_handler(custom, nullptr, record_cmd);
    const mg_cmd cmd = {custom, mg_cstr_array_construct(nullptr, 0)};

    EXPECT_TRUE(mg_run_cmd(&cmd));
    EXPECT_EQ(g_ran_code, custom);

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

TEST(CmdRegistry, UnknownAndUnregisteredCodesAreUnhandled) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);

    const mg_cmd cmd = {static_cast<u32>(MG_CMD_CODE_HELP), mg_cstr_array_construct(nullptr, 0)};
    EXPECT_FALSE(mg_run_cmd(&cmd));

    mg_register_cmd_handler(MG_CMD_CODE_HELP, nullptr, record_cmd);
    mg_unregister_cmd_handler(MG_CMD_CODE_HELP, nullptr, record_cmd);
    EXPECT_FALSE(mg_run_cmd(&cmd));

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

TEST(CmdRegistry, RunsHandlersInOrderUntilOneHandles) {
    mg_cmd_registry cmds = {mg_cmd_binding_list_construct()};
    mg_use_cmd_registry(&cmds);
    g_ran_code = 0;

    /* The refuser registers first and reports unhandled, so the run falls
     * through to the recorder. */
    mg_register_cmd_handler(MG_CMD_CODE_DIRS, nullptr, refuse_cmd);
    mg_register_cmd_handler(MG_CMD_CODE_DIRS, nullptr, record_cmd);
    const mg_cmd cmd = {static_cast<u32>(MG_CMD_CODE_DIRS), mg_cstr_array_construct(nullptr, 0)};

    EXPECT_TRUE(mg_run_cmd(&cmd));
    EXPECT_EQ(g_ran_code, static_cast<u32>(MG_CMD_CODE_DIRS));

    mg_cmd_registry_clear(&cmds);
    mg_use_cmd_registry(nullptr);
}

} // namespace
