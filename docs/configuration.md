# Configuration

Weir's configuration surface is deliberately small: one env var for
logging, one JSON file for the REPL. Scripts read neither — a
script's behaviour is its file's business, which is the property the
language exists to keep.

## `WEIR_LOG`

`Log.trace`/`debug`/`info`/`warn` write levelled diagnostics to
stderr; `WEIR_LOG` selects the level for one run:

```text
WEIR_LOG=debug weir script.weir    # turn the detail on
WEIR_LOG=off weir script.weir      # silence the log
```

`printerr` and `fail` reach you at every level — deliberately, there
is no `Log.error`, because an error an env var can silence is the
one message you needed. Stdout stays byte-identical at every level:
logging never touches the data channel.

## The REPL config

`$XDG_CONFIG_HOME/weir/config.json` (fallback
`~/.config/weir/config.json`; on Windows
`%APPDATA%\weir\config.json`) — read by the REPL only:

| key | default | meaning |
|---|---|---|
| `historySize` | `5000` | entries kept |
| `historyDedup` | `true` | drop consecutive duplicates |
| `historyPath` | `<state>/weir/history` | where history lives (`$XDG_STATE_HOME`, `~/.local/state`, or `%LOCALAPPDATA%`) |
| `finderFlags` | `["--height", "40%", "--reverse"]` | argv extras for the `Ctrl+R` fzf search |
| `echoElems` | `100` | the echo's unforced-element cap ([the REPL](repl.md)) |

An unknown key is warned about with a did-you-mean — a typo
silently doing nothing is the config-file failure mode this
refuses. An absent or unparseable file means defaults.

Also honored everywhere: [`NO_COLOR`](https://no-color.org)
strips the REPL's and the checker's dressing; piped output is
always plain.
