# A REPL under a pseudo-terminal that waits for the prompt, never a fixed
# sleep: every step reads until the expected text appears (bounded), so a
# slow runner is waited for instead of read as a failure.
import os
import pty
import re
import select
import time

_ANSI = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]|\x1b=|\x1b>|\x1b\][^\x07]*\x07")


def clean(b):
    return _ANSI.sub("", b.decode(errors="replace")).replace("\r", "")


class Repl:
    def __init__(self, weir, cwd=None, env=None, argv=None):
        self.pid, self.fd = pty.fork()
        if self.pid == 0:
            if cwd:
                os.chdir(cwd)
            if env:
                os.environ.update(env)
            os.execv(weir, argv or ["weir"])
        self.out = b""

    def _read(self, t):
        r, _, _ = select.select([self.fd], [], [], t)
        if r:
            try:
                c = os.read(self.fd, 65536)
            except OSError:
                return False
            if not c:
                return False
            self.out += c
        return True

    def wait_for(self, pattern, start=0, timeout=15):
        """read until `pattern` (a regex) matches the cleaned output past
        byte `start`; returns the cleaned text, or None on timeout/EOF"""
        deadline = time.time() + timeout
        while time.time() < deadline:
            text = clean(self.out[start:])
            if re.search(pattern, text):
                return text
            if not self._read(0.1):
                return None
        return None

    def prompt(self, start=0, timeout=15):
        return self.wait_for(r"weir> $", start, timeout)

    def send(self, data):
        os.write(self.fd, data if isinstance(data, bytes) else data.encode())

    def submit(self, data, timeout=15):
        """send keys ending in Enter; return the cleaned output up to the
        next prompt"""
        start = len(self.out)
        self.send(data)
        return self.prompt(start, timeout) or clean(self.out[start:])

    def close(self):
        try:
            self.send(b"\x03\x04")
        except OSError:
            pass
        deadline = time.time() + 10
        while time.time() < deadline:
            if not self._read(0.1):
                break
            done, _ = os.waitpid(self.pid, os.WNOHANG)
            if done:
                self.pid = 0
                break
        if self.pid:
            try:
                os.kill(self.pid, 9)
                os.waitpid(self.pid, 0)
            except OSError:
                pass
        try:
            os.close(self.fd)
        except OSError:
            pass
