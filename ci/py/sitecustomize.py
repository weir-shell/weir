# e2e's python3 (on PYTHONPATH from ci/e2e.sh): http.server's HTTPServer
# calls socket.getfqdn(host) after bind and before listen — a reverse DNS
# lookup a macOS runner answers in ~35s, so every local test server came
# up late. Nothing in the e2e needs the FQDN.
import socket

socket.getfqdn = lambda name="": name or "localhost"
