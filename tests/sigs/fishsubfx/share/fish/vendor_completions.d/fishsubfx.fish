# fishsubfx fish completion: clap's shape, each line conditioned on where it applies
function __fish_fishsubfx_using_subcommand
	set -l cmd (__fish_fishsubfx_needs_command)
	test -z "$cmd"
	and return 1
	contains -- $cmd[1] $argv
end

complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -l server-url -d 'Server URL' -r
complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -s q -l quiet -d 'Quiet mode - suppress diagnostic messages'
complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -s h -l help -d 'Print help'
complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -s V -l version -d 'Print version'
complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -f -a "record" -d 'Record a terminal session'
complete -c fishsubfx -n "__fish_fishsubfx_needs_command" -f -a "play" -d 'Replay a terminal session'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand record" -s f -l output-format -d 'Output file format [default: v3]' -r -f
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand record" -s a -l append -d 'Append to an existing recording file'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand record" -l overwrite -d 'Overwrite the output file if it already exists'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand record" -l headless -d 'Headless mode - don\'t use the terminal for I/O'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand record" -s h -l help -d 'Print help'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand play" -s s -l speed -d 'Set playback speed' -r
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand play" -s l -l loop -d 'Loop playback continuously'
complete -c fishsubfx -n "__fish_fishsubfx_using_subcommand play" -s h -l help -d 'Print help'
complete -c fishsubfx -n "__fish_seen_subcommand_from play; and not __fish_seen_subcommand_from help" -l quiet -d 'Quiet mode - suppress diagnostic messages'
