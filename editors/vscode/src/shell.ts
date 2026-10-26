// A shell word for the terminal fallback: the path reaches a shell only
// when the terminal exposes no shell integration (which hands the program
// an argv, so nothing is re-parsed). Every value sent is untrusted — a
// repository chooses its file names, and `$(…)`, backticks and `$VAR` are
// legal in them. POSIX shells expand nothing inside single quotes; a
// literal quote is closed and reopened around. cmd and PowerShell have no
// such quote, so a word they could re-interpret is refused, never guessed.
export function quoteForShell(
  word: string,
  platform: NodeJS.Platform
): string | null {
  if (platform !== "win32") {
    return `'${word.replace(/'/g, "'\\''")}'`;
  }
  if (/[\r\n"%!`$&|<>^]/.test(word)) {
    return null;
  }
  return `"${word}"`;
}
