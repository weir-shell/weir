// The weir Zed extension — glue only, the VS Code client's sibling:
// the server is `weir lsp`, the one command, resolved from PATH.

use zed_extension_api as zed;

struct WeirExtension;

impl zed::Extension for WeirExtension {
    fn new() -> Self {
        WeirExtension
    }

    fn language_server_command(
        &mut self,
        _id: &zed::LanguageServerId,
        worktree: &zed::Worktree,
    ) -> zed::Result<zed::Command> {
        let command = worktree
            .which("weir")
            .ok_or_else(|| "weir not found on PATH (the server is `weir lsp`)".to_string())?;

        Ok(zed::Command {
            command,
            args: vec!["lsp".into()],
            env: Default::default(),
        })
    }
}

zed::register_extension!(WeirExtension);
