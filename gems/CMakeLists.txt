# The gems in this tree, one directory each. Every gem declares itself with
# mg_add_gem, so each is gated behind its own MG_GEM_<NAME> option and
# lands in MG_GEMS_OUTPUT_DIR. SDL is one directory holding three gems, which
# share the one SDL fetched there.
add_subdirectory(SDL)
add_subdirectory(spdlog)
