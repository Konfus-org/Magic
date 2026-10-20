# The last step of a library's build (CMakeLists.txt): finds the shared library it made and copies it to
# built/native/, the one place the gem's build takes it from. The file is at PATH under source/ where the library came
# as an archive holding several, else found by name under built/ (what was installed) then build/ (a target built
# alone). Run as cmake -P with FOLDER, FILE and PATH.
if(PATH)
  set(found "${FOLDER}/source/${PATH}")
  if(NOT EXISTS "${found}")
    message(FATAL_ERROR "${FILE} is not at ${PATH} in what ${FOLDER}/source holds.")
  endif()
else()
  file(GLOB_RECURSE candidates "${FOLDER}/built/${FILE}" "${FOLDER}/build/${FILE}")
  list(FILTER candidates EXCLUDE REGEX "/built/native/")
  if(NOT candidates)
    message(FATAL_ERROR "${FILE} came of nothing under ${FOLDER}/built or ${FOLDER}/build.")
  endif()
  list(GET candidates 0 found)
endif()
file(COPY "${found}" DESTINATION "${FOLDER}/built/native")
