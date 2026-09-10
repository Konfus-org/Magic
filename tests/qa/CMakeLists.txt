# The QA app: a hand-run executable that exercises engine subsystems end to
# end and prints what it saw, where the unit tests assert in-process. Exit
# code 0 means every check passed, so a script can run it too.
mg_add_app(qa)
