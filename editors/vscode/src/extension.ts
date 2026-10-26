// The weir VS Code client — glue only. The server is `weir lsp` as
// shipped; protocol disagreements are FINDINGS against the server
// (frame-level pins), never client-side workarounds — the
// one-pipeline principle applied to clients.
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { LanguageClient, TransportKind } from "vscode-languageclient/node";

let client: LanguageClient | undefined;

// Resolve the weir binary ourselves so failure is an ACTIONABLE
// message, never a bare `spawn ... ENOENT`. GUI editors (macOS
// especially) see a shorter PATH than the user's shell, so the
// default install location is probed after PATH.
function resolveServer(configured: string): string | undefined {
  if (configured.includes("/") || configured.includes(path.sep)) {
    return fs.existsSync(configured) ? configured : undefined;
  }

  const dirs = (process.env.PATH ?? "").split(path.delimiter);
  const home = process.env.HOME ?? "";
  if (home) {
    dirs.push(path.join(home, ".local", "bin"));
  }

  for (const dir of dirs) {
    if (!dir) {
      continue;
    }
    const candidate = path.join(dir, configured);
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  return undefined;
}

export function activate(context: vscode.ExtensionContext): void {
  const cfg = vscode.workspace.getConfiguration("weir");
  // `|| "weir"` not `?? "weir"`: a CLEARED setting arrives as the empty
  // string, which is not nullish — the "command": "" trap
  const configured = (cfg.get<string>("serverPath") ?? "").trim() || "weir";

  const resolved = resolveServer(configured);

  if (!resolved) {
    const hint = configured.includes(" ")
      ? `weir.serverPath is the path to the weir BINARY only ('${configured}' contains a space) — the client runs '<path> lsp' itself; remove the ' lsp' suffix.`
      : `'${configured}' was not found on the extension host's PATH (GUI apps often see a shorter PATH than your shell) nor in ~/.local/bin. Install weir, or set weir.serverPath to the absolute binary path.`;
    void vscode.window
      .showErrorMessage(`weir language server: ${hint}`, "Install weir")
      .then((choice) => {
        if (choice === "Install weir") {
          void vscode.env.openExternal(
            vscode.Uri.parse("https://weir.sh/docs/install/")
          );
        }
      });
    return;
  }

  client = new LanguageClient(
    "weir",
    "weir language server",
    {
      command: resolved,
      args: ["lsp"],
      transport: TransportKind.stdio,
    },
    {
      documentSelector: [{ language: "weir" }],
      // the inlay hint settings travel to the server: the whole `weir`
      // section at initialize and on every change — the client never
      // filters hints itself
      initializationOptions: { inlayHints: cfg.get("inlayHints") },
      synchronize: { configurationSection: "weir" },
    }
  );

  void client.start();

  // run / check / check --can in a reused integrated terminal named
  // `weir`, with the SAME binary the server runs — no code lens, no
  // server-side execution: the server never evaluates
  let terminal: vscode.Terminal | undefined;

  const quote = (s: string): string => `"${s.replace(/"/g, '\\"')}"`;

  const inTerminal = async (args: string[]): Promise<void> => {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== "weir") {
      void vscode.window.showInformationMessage(
        "weir: open a weir script first"
      );
      return;
    }
    if (editor.document.isDirty || editor.document.isUntitled) {
      if (!(await editor.document.save())) {
        return;
      }
    }
    if (!terminal || terminal.exitStatus !== undefined) {
      terminal = vscode.window.createTerminal("weir");
    }
    terminal.show(true);
    terminal.sendText(
      [quote(resolved), ...args, quote(editor.document.uri.fsPath)].join(" ")
    );
  };

  context.subscriptions.push(
    // `weir <file>` runs a script — there is no `run` verb
    vscode.commands.registerCommand("weir.run", () => inTerminal([])),
    vscode.commands.registerCommand("weir.check", () => inTerminal(["check"])),
    vscode.commands.registerCommand("weir.checkCan", () =>
      inTerminal(["check", "--can"])
    ),
    vscode.window.onDidCloseTerminal((t) => {
      if (t === terminal) {
        terminal = undefined;
      }
    })
  );
}

export function deactivate(): Thenable<void> | undefined {
  return client?.stop();
}
