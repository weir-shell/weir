// The weir VS Code client — glue only. The server is `weir lsp` as
// shipped; protocol disagreements are FINDINGS against the server
// (frame-level pins), never client-side workarounds — the
// one-pipeline principle applied to clients.
import * as vscode from "vscode";
import { LanguageClient, TransportKind } from "vscode-languageclient/node";

let client: LanguageClient | undefined;

export function activate(_context: vscode.ExtensionContext): void {
  const serverPath =
    vscode.workspace.getConfiguration("weir").get<string>("serverPath") ??
    "weir";

  client = new LanguageClient(
    "weir",
    "weir language server",
    {
      command: serverPath,
      args: ["lsp"],
      transport: TransportKind.stdio,
    },
    {
      documentSelector: [{ language: "weir" }],
    }
  );

  void client.start();
}

export function deactivate(): Thenable<void> | undefined {
  return client?.stop();
}
