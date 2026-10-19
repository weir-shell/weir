# fishfx fish completion
complete -c fishfx -s e -l regexp -d 'A pattern to search for.' -r -f
complete -c fishfx -s A -l after-context -d 'Show NUM lines after each match.' -r -f
complete -c fishfx  -l binary -d 'Search binary files.'
complete -c fishfx -s u -l unrestricted -d 'Reduce the level of "smart" filtering.'
complete -c fishfx -n '__fish_use_subcommand' -l hidden -d 'Search hidden files.'
complete --command fishfx --short-option 'o' --long-option 'output' --description 'Write to file instead of stdout' --require-parameter
complete --command fishfx --long-option 'compressed' --description 'Request compressed response'
complete --command fishfx --old-option 'plain' --description 'An old-style single-dash word'
