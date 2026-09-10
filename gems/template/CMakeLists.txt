# A gem to copy, not to build: gems/CMakeLists.txt does not add this
# directory, so nothing here configures or compiles. To stand up a new gem:
#   1. Copy gems/template to gems/<Name>.
#   2. Replace every 'template' in the copy, file names included.
#   3. Add add_subdirectory(<Name>) to gems/CMakeLists.txt.

# A dependency fetched at configure time goes here, before mg_add_gem, with
# the MG_GEM_<NAME> option pre-declared so turning the gem off skips the
# download too; gems/SDL/CMakeLists.txt is the worked example.

mg_add_gem(template)
